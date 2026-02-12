using System.Runtime.CompilerServices;

namespace Mediator.Core;

internal static class InternalHelpers
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static object[] MaterializeObjects(IEnumerable<object?> source)
    {
        if (source is object[] oa)
        {
            var outList = new List<object>(oa.Length);
            for (int i = 0; i < oa.Length; i++)
            {
                if (oa[i] != null)
                    outList.Add(oa[i]!);
            }
            return outList.ToArray();
        }

        if (source is System.Collections.ICollection coll)
        {
            var arr = new object[coll.Count];
            int i = 0;
            foreach (var s in source)
            {
                if (s != null) arr[i++] = s!;
            }
            if (i == arr.Length) return arr;
            Array.Resize(ref arr, i);
            return arr;
        }

        var list = new List<object>();
        foreach (var s in source)
        {
            if (s != null) list.Add(s);
        }
        return list.ToArray();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static object[] MaterializeTypes(IEnumerable<object?> source)
    {
        if (source is object[] oa)
        {
            var outList = new List<object>(oa.Length);
            for (int i = 0; i < oa.Length; i++)
            {
                if (oa[i] != null)
                    outList.Add(oa[i]!.GetType());
            }
            return outList.ToArray();
        }

        if (source is System.Collections.ICollection coll)
        {
            var arr = new object[coll.Count];
            int i = 0;
            foreach (var s in source)
            {
                if (s != null) arr[i++] = s.GetType();
            }
            if (i == arr.Length) return arr;
            Array.Resize(ref arr, i);
            return arr;
        }

        var list = new List<object>();
        foreach (var s in source)
        {
            if (s != null) list.Add(s.GetType());
        }
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
