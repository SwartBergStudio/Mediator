using System.Buffers;
using System.IO;
using System.Text.Json;

namespace Mediator.Persistence
{
    /// <summary>
    /// File-based implementation of notification persistence using JSON files.
    /// </summary>
    public class FileNotificationPersistence : INotificationPersistence, INotificationRetryPersistence
    {
        private readonly string _directory;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        
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
        public Task<string> PersistAsync(NotificationWorkItem workItem, CancellationToken cancellationToken = default)
            => WriteNewAsync(workItem, attemptCount: 0, retryAfter: null, exception: null, cancellationToken);

        /// <inheritdoc />
        public Task<string> PersistForRetryAsync(NotificationWorkItem workItem, int attemptCount, DateTime retryAfter, Exception? exception, CancellationToken cancellationToken = default)
            => WriteNewAsync(workItem, attemptCount, retryAfter, exception, cancellationToken);

        private async Task<string> WriteNewAsync(NotificationWorkItem workItem, int attemptCount, DateTime? retryAfter, Exception? exception, CancellationToken cancellationToken)
        {
            if (workItem.NotificationType == null)
                throw new ArgumentException("NotificationType cannot be null", nameof(workItem));
            
            if (string.IsNullOrEmpty(workItem.SerializedNotification))
                throw new ArgumentException("SerializedNotification cannot be null or empty", nameof(workItem));

            var id = Guid.NewGuid().ToString("N");
            var filePath = Path.Combine(_directory, $"{id}.json");

            var record = WriteRecord(
                id,
                createdAt: DateTime.UtcNow,
                retryAfter: retryAfter,
                attemptCount: attemptCount,
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("assemblyQualifiedName", workItem.NotificationType.AssemblyQualifiedName ?? string.Empty);
                    writer.WriteString("serializedNotification", workItem.SerializedNotification);
                    writer.WriteString("createdAt", workItem.CreatedAt);
                    if (workItem.TargetHandlerType != null)
                        writer.WriteString("targetHandlerType", workItem.TargetHandlerType);
                    writer.WriteEndObject();
                },
                lastException: exception?.ToString());

            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                // Written to a temporary name and renamed, so a half-written file is never read as pending.
                var tempPath = filePath + ".tmp";
                await WriteFileAsync(tempPath, record, cancellationToken);
                File.Move(tempPath, filePath);
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

                // Files whose retry time has not arrived are skipped without counting towards the batch,
                // so they cannot starve notifications that are ready.
                foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
                {
                    var item = await ProcessFileAsync(file, cancellationToken);
                    if (item != null)
                    {
                        items.Add(item);
                        if (items.Count >= batchSize) break;
                    }
                }
            }
            finally
            {
                _semaphore.Release();
            }

            return items;
        }

        private static async Task<PersistedNotificationWorkItem?> ProcessFileAsync(string filePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                using var document = await JsonDocument.ParseAsync(stream, default, cancellationToken);

                return ParseJsonToWorkItem(document.RootElement);
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

            // GetDateTime keeps the UTC kind of the ISO 8601 value; DateTime.Parse converted it to local time,
            // which delayed retries by the server's UTC offset.
            var retryAfter = data.TryGetProperty("retryAfter", out var retryProp) && retryProp.ValueKind == JsonValueKind.String
                ? retryProp.GetDateTime().ToUniversalTime()
                : (DateTime?)null;

            if (retryAfter.HasValue && retryAfter.Value > DateTime.UtcNow)
                return null;

            if (!workItemData.TryGetProperty("assemblyQualifiedName", out var typeProp) || 
                typeProp.ValueKind != JsonValueKind.String)
                return null;

            var typeName = typeProp.GetString();
            if (string.IsNullOrEmpty(typeName))
                return null;
                
            var notificationType = NotificationTypeResolver.Resolve(typeName);
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
                    : DateTime.UtcNow,
                TargetHandlerType = workItemData.TryGetProperty("targetHandlerType", out var handlerProp) && handlerProp.ValueKind == JsonValueKind.String
                    ? handlerProp.GetString()
                    : null
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
                byte[] record;
                await using (var readStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None, 4096, true))
                using (var document = await JsonDocument.ParseAsync(readStream, default, cancellationToken))
                {
                    var data = document.RootElement;
                    if (!data.TryGetProperty("attemptCount", out var attemptProp) ||
                        attemptProp.ValueKind != JsonValueKind.Number)
                        return false;

                    var workItem = data.GetProperty("workItem");
                    record = WriteRecord(
                        data.GetProperty("id").GetString()!,
                        createdAt: data.TryGetProperty("createdAt", out var createdProp) && createdProp.ValueKind != JsonValueKind.Undefined
                            ? createdProp.GetDateTime()
                            : DateTime.UtcNow,
                        retryAfter,
                        attemptCount: attemptProp.GetInt32() + 1,
                        workItem.WriteTo,
                        exception?.ToString());
                }

                await WriteFileAsync(filePath, record, cancellationToken);
                return true;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
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

        /// <summary>
        /// Writes a persisted record. The layout matches the camelCase JSON produced by earlier versions,
        /// so files written before an upgrade are still readable and vice versa.
        /// </summary>
        private static byte[] WriteRecord(string id, DateTime createdAt, DateTime? retryAfter, int attemptCount, Action<Utf8JsonWriter> writeWorkItem, string? lastException)
        {
            var buffer = new ArrayBufferWriter<byte>(1024);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("id", id);
                writer.WriteString("createdAt", createdAt);
                if (retryAfter.HasValue) writer.WriteString("retryAfter", retryAfter.Value);
                else writer.WriteNull("retryAfter");
                writer.WriteNumber("attemptCount", attemptCount);
                writer.WritePropertyName("workItem");
                writeWorkItem(writer);
                if (lastException != null) writer.WriteString("lastException", lastException);
                else writer.WriteNull("lastException");
                writer.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
        }

        private static async Task WriteFileAsync(string filePath, byte[] content, CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
            await stream.WriteAsync(content, cancellationToken);
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