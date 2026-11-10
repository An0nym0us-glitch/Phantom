namespace Phantom.SystemMonitoring;

/// <summary>Pure aggregation of Windows "GPU Engine" counter instances; separated for unit testing.</summary>
public static class GpuCounterMath
{
    /// <summary>
    /// Windows exposes one counter instance per (process, adapter, engine), named like
    /// <c>pid_1234_luid_0x00000000_0x0000C5B2_phys_0_eng_3_engtype_3D</c>.
    /// Utilisation of an engine is the sum across processes; the GPU's utilisation is that of its
    /// busiest engine (the same convention Task Manager uses). Returns null when there are no instances.
    /// </summary>
    public static double? AggregateUtilization(IEnumerable<(string Instance, double Value)> instances)
    {
        var perEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, value) in instances)
        {
            string engine = EngineKey(instance);
            perEngine[engine] = perEngine.GetValueOrDefault(engine) + Math.Max(0, value);
        }

        return perEngine.Count == 0 ? null : Math.Clamp(perEngine.Values.Max(), 0, 100);
    }

    /// <summary>Strips the leading <c>pid_N_</c> so instances from different processes share a key.</summary>
    public static string EngineKey(string instance)
    {
        int luid = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        return luid >= 0 ? instance[luid..] : instance;
    }
}
