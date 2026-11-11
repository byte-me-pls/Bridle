using System.Runtime.InteropServices;
using System.Text;

namespace Bridle.Native;

/// <summary>
/// Bridle ABI'sini (native/include/bridle.h) dışa açan bir DLL'in sarmalayıcısı.
/// Yalnızca host sürecinde kullanılır: arayüz süreci hiçbir zaman yabancı
/// yerel kod yüklemez.
/// </summary>
internal sealed unsafe class BridleLibrary : IDisposable
{
    public const int SupportedAbi = 1;

    private nint _handle;
    private readonly delegate* unmanaged[Cdecl]<int> _abiVersion;
    private readonly delegate* unmanaged[Cdecl]<byte*> _describe;
    private readonly delegate* unmanaged[Cdecl]<byte*, byte*, uint, byte*, byte*> _run;
    private readonly delegate* unmanaged[Cdecl]<byte*, void> _free;

    private BridleLibrary(nint handle)
    {
        _handle = handle;
        _abiVersion = (delegate* unmanaged[Cdecl]<int>)Export("bridle_abi_version");
        _describe = (delegate* unmanaged[Cdecl]<byte*>)Export("bridle_describe");
        _run = (delegate* unmanaged[Cdecl]<byte*, byte*, uint, byte*, byte*>)Export("bridle_run");
        _free = (delegate* unmanaged[Cdecl]<byte*, void>)Export("bridle_free");
    }

    public static BridleLibrary Load(string path)
    {
        nint handle = NativeLibrary.Load(path);
        try
        {
            return new BridleLibrary(handle);
        }
        catch
        {
            NativeLibrary.Free(handle);
            throw;
        }
    }

    private nint Export(string name)
    {
        if (!NativeLibrary.TryGetExport(_handle, name, out nint address))
        {
            throw new InvalidOperationException(
                $"Bu DLL Bridle ABI'sini dışa açmıyor: '{name}' bulunamadı.");
        }
        return address;
    }

    public int AbiVersion => _abiVersion();

    public string Describe()
    {
        byte* text = _describe();
        return text == null ? "{}" : Marshal.PtrToStringUTF8((nint)text) ?? "{}";
    }

    /// <summary>Aşamayı çalıştırır; DLL'in döndürdüğü JSON'u verir.</summary>
    public string Run(string stage, string source, string options)
    {
        byte[] stageBytes = NulTerminated(stage);
        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
        byte[] optionBytes = NulTerminated(options);

        fixed (byte* pStage = stageBytes)
        fixed (byte* pOptions = optionBytes)
        fixed (byte* pSource = sourceBytes)
        {
            // Boş dizide fixed işaretçisi null olur; DLL bunu boş kaynak sayar.
            byte* result = _run(pStage, pSource, (uint)sourceBytes.Length, pOptions);
            if (result == null)
            {
                throw new InvalidOperationException("DLL yanıt üretemedi (bellek yetersiz).");
            }
            try
            {
                return Marshal.PtrToStringUTF8((nint)result) ?? "";
            }
            finally
            {
                _free(result);
            }
        }
    }

    private static byte[] NulTerminated(string text)
    {
        int n = Encoding.UTF8.GetByteCount(text);
        byte[] bytes = new byte[n + 1];
        Encoding.UTF8.GetBytes(text, bytes);
        return bytes;
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            NativeLibrary.Free(_handle);
            _handle = 0;
        }
    }
}
