using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace JPVision.Sdk;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeImageInfo
{
    public uint Size, Width, Height, Stride, PixelFormat, Reserved;
    public ulong Length;
    public int OriginX, OriginY;
    public static NativeImageInfo Empty => new() { Size = (uint)Marshal.SizeOf<NativeImageInfo>() };
}

internal abstract class VisionHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    protected VisionHandle(IntPtr value) : base(true) => SetHandle(value);
}
internal sealed class EngineHandle(IntPtr value) : VisionHandle(value)
{
    protected override bool ReleaseHandle()
    {
        Native.EngineRelease(handle);
        return true;
    }
}
internal sealed class RecipeHandle(IntPtr value) : VisionHandle(value)
{
    protected override bool ReleaseHandle()
    {
        Native.RecipeRelease(handle);
        return true;
    }
}
internal sealed class SessionHandle(IntPtr value) : VisionHandle(value)
{
    protected override bool ReleaseHandle()
    {
        Native.SessionRelease(handle);
        return true;
    }
}
internal sealed class FrameHandle(IntPtr value) : VisionHandle(value)
{
    protected override bool ReleaseHandle()
    {
        Native.FrameRelease(handle);
        return true;
    }
}
internal sealed class RunHandle(IntPtr value) : VisionHandle(value)
{
    protected override bool ReleaseHandle()
    {
        Native.RunRelease(handle);
        return true;
    }
}
internal sealed class ResultHandle(IntPtr value) : VisionHandle(value)
{
    protected override bool ReleaseHandle()
    {
        Native.ResultRelease(handle);
        return true;
    }
}

internal static partial class Native
{
    private const string Library = "JPVision.Runtime.dll";
    [LibraryImport(Library, EntryPoint = "vision_version")] internal static partial IntPtr Version();
    [LibraryImport(Library, EntryPoint = "vision_diagnostics")] internal static partial int Diagnostics(out IntPtr text);
    [LibraryImport(Library, EntryPoint = "vision_node_catalog")] internal static partial int NodeCatalog(out IntPtr text);
    [LibraryImport(Library, EntryPoint = "vision_last_error")] private static partial IntPtr LastError();
    [LibraryImport(Library, EntryPoint = "vision_string_free")] private static partial void StringFree(IntPtr text);
    [LibraryImport(Library, EntryPoint = "vision_machine_request")] internal static partial int MachineRequest(out IntPtr text);
    [LibraryImport(Library, EntryPoint = "vision_license_status", StringMarshalling = StringMarshalling.Utf8)] internal static partial int LicenseStatus(string? store, string feature, out IntPtr text);
    [LibraryImport(Library, EntryPoint = "vision_license_import", StringMarshalling = StringMarshalling.Utf8)] internal static partial int LicenseImport(string? store, string file, out IntPtr text);
    [LibraryImport(Library, EntryPoint = "vision_engine_create", StringMarshalling = StringMarshalling.Utf8)] internal static partial int EngineCreate(string options, out IntPtr handle);
    [LibraryImport(Library, EntryPoint = "vision_engine_release")] internal static partial void EngineRelease(IntPtr handle);
    [LibraryImport(Library, EntryPoint = "vision_recipe_load", StringMarshalling = StringMarshalling.Utf8)] internal static partial int RecipeLoad(EngineHandle engine, string directory, out IntPtr handle);
    [LibraryImport(Library, EntryPoint = "vision_recipe_release")] internal static partial void RecipeRelease(IntPtr handle);
    [LibraryImport(Library, EntryPoint = "vision_recipe_description")] internal static partial int RecipeDescription(RecipeHandle recipe, out IntPtr text);
    [LibraryImport(Library, EntryPoint = "vision_session_create")] internal static partial int SessionCreate(RecipeHandle recipe, out IntPtr handle);
    [LibraryImport(Library, EntryPoint = "vision_session_release")] internal static partial void SessionRelease(IntPtr handle);
    [LibraryImport(Library, EntryPoint = "vision_frame_copy")] internal static unsafe partial int FrameCopy(in NativeImageInfo info, byte* bytes, out IntPtr handle);
    [LibraryImport(Library, EntryPoint = "vision_frame_info")] internal static partial int FrameInfo(FrameHandle frame, ref NativeImageInfo info);
    [LibraryImport(Library, EntryPoint = "vision_frame_pixels")] internal static unsafe partial int FramePixels(FrameHandle frame, byte* target, ulong size);
    [LibraryImport(Library, EntryPoint = "vision_frame_release")] internal static partial void FrameRelease(IntPtr handle);
    [LibraryImport(Library, EntryPoint = "vision_run_create", StringMarshalling = StringMarshalling.Utf8)] internal static partial int RunCreate(SessionHandle session, FrameHandle frame, string request, out IntPtr handle);
    [LibraryImport(Library, EntryPoint = "vision_run_execute")] internal static partial int RunExecute(RunHandle run, out IntPtr handle);
    [LibraryImport(Library, EntryPoint = "vision_run_cancel")] internal static partial void RunCancel(RunHandle run);
    [LibraryImport(Library, EntryPoint = "vision_run_release")] internal static partial void RunRelease(IntPtr handle);
    [LibraryImport(Library, EntryPoint = "vision_result_json")] internal static partial int ResultJson(ResultHandle result, out IntPtr text);
    [LibraryImport(Library, EntryPoint = "vision_result_frame", StringMarshalling = StringMarshalling.Utf8)] internal static partial int ResultFrame(ResultHandle result, string node, out IntPtr handle);
    [LibraryImport(Library, EntryPoint = "vision_result_input_frame", StringMarshalling = StringMarshalling.Utf8)] internal static partial int ResultInputFrame(ResultHandle result, string node, out IntPtr handle);
    [LibraryImport(Library, EntryPoint = "vision_result_output_frame", StringMarshalling = StringMarshalling.Utf8)] internal static partial int ResultOutputFrame(ResultHandle result, string name, out IntPtr handle);
    [LibraryImport(Library, EntryPoint = "vision_result_release")] internal static partial void ResultRelease(IntPtr handle);
    [LibraryImport(Library, EntryPoint = "vision_recipe_seal", StringMarshalling = StringMarshalling.Utf8)] internal static partial int RecipeSeal(string json, out IntPtr text);
    [LibraryImport(Library, EntryPoint = "vision_recipe_validate", StringMarshalling = StringMarshalling.Utf8)] internal static partial int RecipeValidate(EngineHandle engine, string json, out IntPtr text);

    internal static void Check(int status)
    {
        if (status == 0)
            return;
        string message = Marshal.PtrToStringUTF8(LastError()) ?? "Native operation failed.";
        if (status >= 100)
            throw new VisionLicenseException(status, message);
        if (status == 1)
            throw new ArgumentException(message);
        if (status == 4)
            throw new ObjectDisposedException("VisionSession", message);
        throw new VisionException(status, message);
    }
    internal static string TakeString(int status, IntPtr text)
    {
        try
        {
            Check(status);
            return Marshal.PtrToStringUTF8(text) ?? throw new InvalidDataException("Missing native response.");
        }
        finally { if (text != IntPtr.Zero) StringFree(text); }
    }
    internal static JsonElement TakeJson(int status, IntPtr text)
    {
        try
        {
            Check(status);
            return ReadJson(text);
        }
        finally { if (text != IntPtr.Zero) StringFree(text); }
    }
    private static unsafe JsonElement ReadJson(IntPtr text)
    {
        if (text == IntPtr.Zero)
            throw new InvalidDataException("Missing native response.");
        var utf8 = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)text);
        var reader = new Utf8JsonReader(utf8);
        // ParseValue copies the UTF-8 data into an owning element before the native buffer is freed.
        var value = JsonElement.ParseValue(ref reader);
        if (reader.Read())
            throw new JsonException("Unexpected trailing native JSON data.");
        return value;
    }
}
