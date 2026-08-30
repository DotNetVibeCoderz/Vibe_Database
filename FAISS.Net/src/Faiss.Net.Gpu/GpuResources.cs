using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime.OpenCL;

namespace Faiss.Net.Gpu;

/// <summary>
/// Owns the ILGPU context and accelerator that GPU indexes run on — the analogue of
/// <c>faiss.StandardGpuResources()</c>.
/// <para>
/// Creating an accelerator and compiling kernels costs hundreds of milliseconds, so one instance
/// should be created per process and shared by every GPU index; <see cref="Default"/> does exactly
/// that. Kernels are compiled once on first use and cached here.
/// </para>
/// <para>
/// Device selection prefers CUDA, then OpenCL, then ILGPU's CPU accelerator. The CPU accelerator is
/// a real fallback rather than a stub: the same kernels run, so code written against a GPU index
/// keeps working on a machine without one — just without the speedup. Check
/// <see cref="IsHardwareAccelerated"/> before drawing conclusions from a benchmark.
/// </para>
/// </summary>
public sealed class StandardGpuResources : IDisposable
{
    private static readonly Lazy<StandardGpuResources> Shared = new(() => new StandardGpuResources());

    private readonly Context _context;
    private bool _disposed;

    /// <summary>Process-wide shared resources. Created on first use and never disposed.</summary>
    public static StandardGpuResources Default => Shared.Value;

    /// <summary>The accelerator kernels are launched on.</summary>
    public Accelerator Accelerator { get; }

    /// <summary>True when a real GPU was found; false when running on the CPU fallback accelerator.</summary>
    public bool IsHardwareAccelerated { get; }

    /// <summary>Human-readable device description, for logging and benchmark headers.</summary>
    public string DeviceName => $"{Accelerator.Name} ({Accelerator.AcceleratorType})";

    /// <summary>Device memory in bytes.</summary>
    public long DeviceMemory => Accelerator.MemorySize;

    /// <summary>
    /// Cap on the distance matrix held on the device at once. Search splits the query batch so that
    /// <c>chunk * ntotal * 4</c> bytes stays under this, which is what lets a database far larger
    /// than device memory still be searched in one call.
    /// </summary>
    public long MaxDistanceMatrixBytes { get; set; } = 256L * 1024 * 1024;

    /// <param name="preferCpu">Force the CPU accelerator, mainly for testing kernel correctness.</param>
    public StandardGpuResources(bool preferCpu = false)
    {
        _context = Context.Create(builder => builder.Default());

        Device? device = null;
        if (!preferCpu)
        {
            device = _context.Devices.FirstOrDefault(d => d is CudaDevice)
                  ?? _context.Devices.FirstOrDefault(d => d is CLDevice);
        }
        device ??= _context.Devices.FirstOrDefault(d => d is CPUDevice)
                ?? _context.Devices.First();

        Accelerator = device.CreateAccelerator(_context);
        IsHardwareAccelerated = device is CudaDevice or CLDevice;
    }

    /// <summary>Every accelerator ILGPU can see, for reporting and for multi-GPU replica setups.</summary>
    public static IReadOnlyList<string> EnumerateDevices()
    {
        using var context = Context.Create(builder => builder.Default());
        return [.. context.Devices.Select(d => $"{d.Name} ({d.AcceleratorType}, {d.MemorySize / (1024 * 1024)}MB)")];
    }

    /// <summary>True when at least one CUDA or OpenCL device is present.</summary>
    public static bool IsGpuAvailable()
    {
        try
        {
            using var context = Context.Create(builder => builder.Default());
            return context.Devices.Any(d => d is CudaDevice or CLDevice);
        }
        catch
        {
            // A missing or mismatched driver surfaces as an exception during enumeration; that is
            // simply "no GPU here", not an error the caller needs to handle.
            return false;
        }
    }

    /// <summary>Creates one resource object per detected GPU, for <see cref="IndexReplicas"/>.</summary>
    public static IReadOnlyList<StandardGpuResources> ForEachGpu()
    {
        var resources = new List<StandardGpuResources>();
        using var probe = Context.Create(builder => builder.Default());
        int count = probe.Devices.Count(d => d is CudaDevice or CLDevice);
        for (int i = 0; i < count; i++) resources.Add(new StandardGpuResources(deviceIndex: i));
        return resources;
    }

    private StandardGpuResources(int deviceIndex)
    {
        _context = Context.Create(builder => builder.Default());
        var gpus = _context.Devices.Where(d => d is CudaDevice or CLDevice).ToArray();
        var device = gpus.Length > deviceIndex ? gpus[deviceIndex] : _context.Devices.First();
        Accelerator = device.CreateAccelerator(_context);
        IsHardwareAccelerated = device is CudaDevice or CLDevice;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Accelerator.Dispose();
        _context.Dispose();
    }
}
