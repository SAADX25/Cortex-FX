using System.Runtime.InteropServices;
using System.Text;

namespace CortexFX.Core.Audio;

public sealed record AudioGpuDevice(bool Available, string Name, string Reason);

/// <summary>Checks the NVIDIA driver directly; does not require nvidia-smi or a CUDA Toolkit installation.</summary>
public static class AudioGpuDeviceDetector
{
    [DllImport("nvcuda.dll", CallingConvention = CallingConvention.Winapi)] private static extern int cuInit(uint flags);
    [DllImport("nvcuda.dll", CallingConvention = CallingConvention.Winapi)] private static extern int cuDeviceGetCount(out int count);
    [DllImport("nvcuda.dll", CallingConvention = CallingConvention.Winapi)] private static extern int cuDeviceGet(out int device, int ordinal);
    [DllImport("nvcuda.dll", CallingConvention = CallingConvention.Winapi)] private static extern int cuDeviceGetName(byte[] name, int length, int device);

    public static AudioGpuDevice Detect()
    {
        try
        {
            if (cuInit(0) != 0 || cuDeviceGetCount(out int count) != 0 || count == 0 || cuDeviceGet(out int device, 0) != 0)
                return new(false, "", "No compatible NVIDIA GPU is available. Choose CPU.");
            byte[] name = new byte[256];
            string label = cuDeviceGetName(name, name.Length, device) == 0
                ? Encoding.UTF8.GetString(name).TrimEnd('\0') : "NVIDIA GPU";
            return new(true, label, "NVIDIA GPU detected");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new(false, "", "GPU acceleration requires an NVIDIA GPU and driver. Choose CPU.");
        }
    }
}

public sealed class AudioGpuUnavailableException(string message, Exception? innerException = null) : InvalidOperationException(message, innerException);