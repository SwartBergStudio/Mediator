using System.Runtime.CompilerServices;

namespace Mediator.Core;

internal static class InternalHelpers
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static object[] MaterializeObjects(IEnumerable<object?> source)
    {
        if (source is object[] oa)
        {
            int nonNull = 0;
            for (int i = 0; i < oa.Length; i++)
            {
                if (oa[i] != null) nonNull++;
            }

            if (nonNull == 0) return Array.Empty<object>();
            if (nonNull == oa.Length) return oa;

            var result = new object[nonNull];
            int j = 0;
            for (int i = 0; i < oa.Length; i++)
            {
                if (oa[i] != null) result[j++] = oa[i]!;
            }
            return result;
        }

        if (source is System.Collections.ICollection coll)
        {
            if (coll.Count == 0) return Array.Empty<object>();

            var arr = new object[coll.Count];
            int i = 0;
            foreach (var s in source)
            {
                if (s != null) arr[i++] = s!;
            }
            if (i == 0) return Array.Empty<object>();
            if (i == arr.Length) return arr;
            Array.Resize(ref arr, i);
            return arr;
        }

        var list = new List<object>();
        foreach (var s in source)
        {
            if (s != null) list.Add(s);
        }
        if (list.Count == 0) return Array.Empty<object>();
        return list.ToArray();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static object[] MaterializeTypes(IEnumerable<object?> source)
    {
        if (source is object[] oa)
        {
            int nonNull = 0;
            for (int i = 0; i < oa.Length; i++)
            {
                if (oa[i] != null) nonNull++;
            }

            if (nonNull == 0) return Array.Empty<object>();

            var result = new object[nonNull];
            int j = 0;
            for (int i = 0; i < oa.Length; i++)
            {
                if (oa[i] != null) result[j++] = oa[i]!.GetType();
            }
            return result;
        }

        if (source is System.Collections.ICollection coll)
        {
            if (coll.Count == 0) return Array.Empty<object>();

            var arr = new object[coll.Count];
            int i = 0;
            foreach (var s in source)
            {
                if (s != null) arr[i++] = s.GetType();
            }
            if (i == 0) return Array.Empty<object>();
            if (i == arr.Length) return arr;
            Array.Resize(ref arr, i);
            return arr;
        }

        var list = new List<object>();
        foreach (var s in source)
        {
            if (s != null) list.Add(s.GetType());
        }
        if (list.Count == 0) return Array.Empty<object>();
        return list.ToArray();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static async Task AwaitConfigurable(Task task, bool useConfigureAwait)
    {
        if (useConfigureAwait)
            await task.ConfigureAwait(false);
        else
            await task;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static async Task<T> AwaitConfigurable<T>(Task<T> task, bool useConfigureAwait)
    {
        if (useConfigureAwait)
            return await task.ConfigureAwait(false);
        else
            return await task;
    }
}
