using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Mediator.Persistence
{
    /// <summary>
    /// File-based implementation of notification persistence using JSON files.
    /// </summary>
    public class FileNotificationPersistence : INotificationPersistence
    {
        private readonly string _directory;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultBufferSize = 4096
        };

        /// <summary>
        /// Initializes a new instance of the FileNotificationPersistence class.
        /// </summary>
        public FileNotificationPersistence(string? directory = null)
        {
            _directory = directory ?? Path.Combine(Directory.GetCurrentDirectory(), "mediator-notifications");
            
            if (!Directory.Exists(_directory))
            {
                Directory.CreateDirectory(_directory);
            }
        }

        /// <inheritdoc />
        public async Task<string> PersistAsync(NotificationWorkItem workItem, CancellationToken cancellationToken = default)
        {
            if (workItem.NotificationType == null)
                throw new ArgumentException("NotificationType cannot be null", nameof(workItem));
            
            if (string.IsNullOrEmpty(workItem.SerializedNotification))
                throw new ArgumentException("SerializedNotification cannot be null or empty", nameof(workItem));

            var id = Guid.NewGuid().ToString("N");
            var filePath = Path.Combine(_directory, $"{id}.json");

            var data = new
            {
                Id = id,
                CreatedAt = DateTime.UtcNow,
                RetryAfter = (DateTime?)null,
                AttemptCount = 0,
                WorkItem = new
                {
                    AssemblyQualifiedName = workItem.NotificationType.AssemblyQualifiedName ?? string.Empty,
                    SerializedNotification = workItem.SerializedNotification,
                    CreatedAt = workItem.CreatedAt
                },
                LastException = (string?)null
            };

            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
                await JsonSerializer.SerializeAsync(stream, data, JsonOptions, cancellationToken);
            }
            finally
            {
                _semaphore.Release();
            }

            return id;
        }

        /// <inheritdoc />
        public async Task<IEnumerable<PersistedNotificationWorkItem>> GetPendingAsync(int batchSize = 100, CancellationToken cancellationToken = default)
        {
            if (batchSize <= 0)
                throw new ArgumentException("Batch size must be greater than zero", nameof(batchSize));

            var items = new List<PersistedNotificationWorkItem>(batchSize);

            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                if (!Directory.Exists(_directory))
                    return items;

                var files = Directory.EnumerateFiles(_directory, "*.json").Take(batchSize);
                var tasks = files.Select(f => ProcessFileAsync(f, cancellationToken)).ToArray();
                
                if (tasks.Length > 0)
                {
                    var results = await Task.WhenAll(tasks);
                    items.AddRange(results.Where(r => r != null)!);
                }
            }
            finally
            {
                _semaphore.Release();
            }

            return items;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task<PersistedNotificationWorkItem?> ProcessFileAsync(string filePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                var data = await JsonSerializer.DeserializeAsync<JsonElement>(stream, JsonOptions, cancellationToken);
                
                return ParseJsonToWorkItem(data);
            }
            catch (JsonException)
            {
                SafeDeleteFile(filePath);
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static PersistedNotificationWorkItem? ParseJsonToWorkItem(JsonElement data)
        {
            if (!data.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
                return null;

            if (!data.TryGetProperty("workItem", out var workItemData))
                return null;

            var retryAfter = data.TryGetProperty("retryAfter", out var retryProp) && retryProp.ValueKind != JsonValueKind.Null
                ? DateTime.Parse(retryProp.GetString()!)
                : (DateTime?)null;

            if (retryAfter.HasValue && retryAfter.Value > DateTime.UtcNow)
                return null;

            if (!workItemData.TryGetProperty("assemblyQualifiedName", out var typeProp) || 
                typeProp.ValueKind != JsonValueKind.String)
                return null;

            var typeName = typeProp.GetString();
            if (string.IsNullOrEmpty(typeName))
                return null;
                
            var notificationType = Type.GetType(typeName);
            if (notificationType == null) 
                return null;

            if (!workItemData.TryGetProperty("serializedNotification", out var serializedProp) ||
                serializedProp.ValueKind != JsonValueKind.String)
                return null;

            var serializedNotification = serializedProp.GetString();
            if (string.IsNullOrEmpty(serializedNotification))
                return null;

            var workItem = new NotificationWorkItem
            {
                NotificationType = notificationType,
                SerializedNotification = serializedNotification,
                CreatedAt = workItemData.TryGetProperty("createdAt", out var createdProp) && createdProp.ValueKind != JsonValueKind.Undefined 
                    ? createdProp.GetDateTime() 
                    : DateTime.UtcNow
            };

            return new PersistedNotificationWorkItem
            {
                Id = idProp.GetString()!,
                WorkItem = workItem,
                CreatedAt = data.TryGetProperty("createdAt", out var itemCreatedProp) && itemCreatedProp.ValueKind != JsonValueKind.Undefined 
                    ? itemCreatedProp.GetDateTime() 
                    : DateTime.UtcNow,
                RetryAfter = retryAfter,
                AttemptCount = data.TryGetProperty("attemptCount", out var attemptProp) && attemptProp.ValueKind == JsonValueKind.Number 
                    ? attemptProp.GetInt32() 
                    : 0
            };
        }

        /// <inheritdoc />
        public async Task CompleteAsync(string id, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("ID cannot be null or empty", nameof(id));

            var filePath = Path.Combine(_directory, $"{id}.json");
            
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                SafeDeleteFile(filePath);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <inheritdoc />
        public async Task FailAsync(string id, Exception exception, DateTime? retryAfter = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("ID cannot be null or empty", nameof(id));

            var filePath = Path.Combine(_directory, $"{id}.json");
            
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                if (!File.Exists(filePath))
                    return;

                var updateSuccessful = await UpdateFailedNotificationFile(filePath, exception, retryAfter, cancellationToken);
                if (!updateSuccessful)
                {
                    SafeDeleteFile(filePath);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private static async Task<bool> UpdateFailedNotificationFile(string filePath, Exception? exception, DateTime? retryAfter, CancellationToken cancellationToken)
        {
            try
            {
                JsonElement data;
                await using (var readStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None, 4096, true))
                {
                    data = await JsonSerializer.DeserializeAsync<JsonElement>(readStream, JsonOptions, cancellationToken);
                }

                if (!data.TryGetProperty("attemptCount", out var attemptProp) || 
                    attemptProp.ValueKind != JsonValueKind.Number)
                    return false;

                var updated = new
                {
                    Id = data.GetProperty("id").GetString(),
                    CreatedAt = data.TryGetProperty("createdAt", out var createdProp) && createdProp.ValueKind != JsonValueKind.Undefined 
                        ? createdProp.GetDateTime() 
                        : DateTime.UtcNow,
                    RetryAfter = retryAfter,
                    AttemptCount = attemptProp.GetInt32() + 1,
                    WorkItem = data.GetProperty("workItem"),
                    LastException = exception?.ToString()
                };

                await using var writeStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
                await JsonSerializer.SerializeAsync(writeStream, updated, JsonOptions, cancellationToken);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <inheritdoc />
        public async Task CleanupAsync(DateTime olderThan, CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                if (!Directory.Exists(_directory))
                    return;

                var files = Directory.EnumerateFiles(_directory, "*.json");
                
                await Parallel.ForEachAsync(files, 
                    new ParallelOptions 
                    { 
                        CancellationToken = cancellationToken, 
                        MaxDegreeOfParallelism = Environment.ProcessorCount 
                    },
                    (filePath, ct) =>
                    {
                        if (File.Exists(filePath) && File.GetCreationTimeUtc(filePath) < olderThan)
                        {
                            SafeDeleteFile(filePath);
                        }
                        return ValueTask.CompletedTask;
                    });
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private static void SafeDeleteFile(string filePath)
        {
            if (!File.Exists(filePath))
                return;

            try
            {
                File.Delete(filePath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>
        /// Disposes the file persistence resources.
        /// </summary>
        public void Dispose()
        {
            _semaphore?.Dispose();
        }
    }
}