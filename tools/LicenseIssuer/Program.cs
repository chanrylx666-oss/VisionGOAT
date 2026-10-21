using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace JPVision.LicenseIssuer;

internal static class Program
{
    private static string Arg(string[] args, string name, string? fallback = null)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1]
            : fallback ?? throw new ArgumentException($"Missing {name}");
    }

    private static JsonObject ReadJson(string path)
    {
        if (new FileInfo(path).Length > 65536)
            throw new ArgumentException("Input exceeds 64 KiB.");
        byte[] bytes = File.ReadAllBytes(path);
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        CheckDuplicates(document.RootElement);
        return JsonNode.Parse(bytes)!.AsObject();
    }

    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new ArgumentException("Duplicate JSON field.");
                CheckDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray())
                CheckDuplicates(child);
    }

    private static void WriteJson(string path, JsonNode value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static void RestrictToOwner(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Windows identity unavailable.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(sid);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static ECDsa LoadKey(string path, out string keyId)
    {
        JsonObject stored = ReadJson(path);
        keyId = stored["keyId"]!.GetValue<string>();
        byte[] encrypted = Convert.FromBase64String(stored["protectedPkcs8"]!.GetValue<string>());
        byte[] privateBytes = WindowsProtection.Unprotect(encrypted);
        try
        {
            ECDsa key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(privateBytes, out int consumed);
            if (consumed != privateBytes.Length || key.KeySize != 256)
            {
                key.Dispose();
                throw new CryptographicException("Expected P-256 key.");
            }
            return key;
        }
        finally { CryptographicOperations.ZeroMemory(privateBytes); }
    }

    private static JsonObject PublicKey(ECDsa key, string keyId)
    {
        ECParameters parameters = key.ExportParameters(false);
        return new JsonObject
        {
            ["keyId"] = keyId,
            ["x"] = Convert.ToBase64String(parameters.Q.X!),
            ["y"] = Convert.ToBase64String(parameters.Q.Y!)
        };
    }
    private static char[] ReadPassword()
    {
        Console.Write("Encryption password: ");
        var password = new List<char>();
        while (true)
        {
            var k = Console.ReadKey(true);
            if (k.Key == ConsoleKey.Enter)
                break;
            if (k.Key == ConsoleKey.Backspace)
            {
                if (password.Count > 0)
                    password.RemoveAt(password.Count - 1);
            }
            else if (!char.IsControl(k.KeyChar))
                password.Add(k.KeyChar);
        }
        Console.WriteLine();
        var characters = password.ToArray();
        password.Clear();
        return characters;
    }
    private static JsonObject Sign(ECDsa key, JsonObject payload)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        return new JsonObject { ["payload"] = Convert.ToBase64String(bytes), ["signature"] = Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) };
    }

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
                throw new ArgumentException("Commands: keygen, issue, inspect, export-header, backup");
            switch (args[0])
            {
                case "keygen":
                    {
                        string store = Arg(args, "--key");
                        string id = Arg(args, "--id", "JPVision-owner-v1");
                        if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,80}$"))
                            throw new ArgumentException("Invalid key ID.");
                        ECDsa key;
                        if (File.Exists(store))
                            key = LoadKey(store, out id);
                        else
                        {
                            key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                            byte[] privateBytes = key.ExportPkcs8PrivateKey();
                            try
                            {
                                WriteJson(store, new JsonObject
                                {
                                    ["version"] = 1,
                                    ["keyId"] = id,
                                    ["protectedPkcs8"] = Convert.ToBase64String(WindowsProtection.Protect(privateBytes))
                                });
                                RestrictToOwner(store);
                            }
                            finally { CryptographicOperations.ZeroMemory(privateBytes); }
                        }
                        using (key)
                            WriteJson(Arg(args, "--public"), PublicKey(key, id));
                        Console.WriteLine($"Public key exported; signing key protected by the current Windows account. Key ID: {id}");
                        break;
                    }
                case "export-header":
                    {
                        JsonObject data = ReadJson(Arg(args, "--public"));
                        string id = data["keyId"]!.GetValue<string>();
                        if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,80}$"))
                            throw new ArgumentException("Invalid key ID.");
                        byte[] x = Convert.FromBase64String(data["x"]!.GetValue<string>()), y = Convert.FromBase64String(data["y"]!.GetValue<string>());
                        if (x.Length != 32 || y.Length != 32)
                            throw new ArgumentException("Expected P-256 public coordinates.");
                        string Bytes(byte[] bytes) => string.Join(",", bytes.Select(b => $"0x{b:X2}"));
                        string header = $"#pragma once\n#include <array>\n#include <cstdint>\nnamespace jp {{\ninline constexpr char TrustKeyId[] = \"{id}\";\ninline constexpr std::array<uint8_t,32> TrustX = {{{Bytes(x)}}};\ninline constexpr std::array<uint8_t,32> TrustY = {{{Bytes(y)}}};\n}}\n";
                        string output = Arg(args, "--out");
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                        File.WriteAllText(output, header, new UTF8Encoding(false));
                        Console.WriteLine("Native public trust anchor exported.");
                        break;
                    }
                case "issue":
                    {
                        JsonObject request = ReadJson(Arg(args, "--request"));
                        if (request["requestVersion"]?.GetValue<int>() != 1)
                            throw new ArgumentException("Unsupported request version.");
                        if (request["productId"]?.GetValue<string>() != "JP-Vision")
                            throw new ArgumentException("Wrong product request.");
                        JsonObject binding = request["machineBinding"]!.AsObject();
                        string profile = binding["profile"]!.GetValue<string>();
                        if (binding["fingerprintVersion"]!.GetValue<int>() != 1 || profile is not ("smbios-uuid" or "baseboard"))
                            throw new ArgumentException("Unsupported machine binding.");
                        if (!Regex.IsMatch(binding["fingerprint"]!.GetValue<string>(), "^[a-f0-9]{64}$"))
                            throw new ArgumentException("Invalid fingerprint.");
                        string customer = Arg(args, "--customer", "JP");
                        if (customer.Length is < 1 or > 128)
                            throw new ArgumentException("Customer ID length invalid.");
                        using ECDsa key = LoadKey(Arg(args, "--key"), out string id);
                        var payload = new JsonObject
                        {
                            ["licenseFormatVersion"] = 1,
                            ["keyId"] = id,
                            ["productId"] = "JP-Vision",
                            ["licenseId"] = Guid.NewGuid().ToString(),
                            ["customer"] = customer,
                            ["grantType"] = "perpetual",
                            ["issuedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                            ["machineBinding"] = binding.DeepClone(),
                            ["features"] = new JsonArray("studio", "runtime")
                        };
                        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
                        byte[] signature = key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                        WriteJson(Arg(args, "--out"), new JsonObject { ["payload"] = Convert.ToBase64String(bytes), ["signature"] = Convert.ToBase64String(signature) });
                        Console.WriteLine($"Perpetual machine license issued for {customer}. License ID: {payload["licenseId"]}");
                        break;
                    }
                case "inspect":
                    {
                        JsonObject envelope = ReadJson(Arg(args, "--license"));
                        using JsonDocument payload = JsonDocument.Parse(Convert.FromBase64String(envelope["payload"]!.GetValue<string>()));
                        Console.WriteLine(payload.RootElement.GetRawText());
                        Console.WriteLine("Information only; inspect is not a signature or machine authorization check.");
                        break;
                    }
                case "backup":
                    {
                        string output = Arg(args, "--out");
                        if (File.Exists(output))
                            throw new IOException("Backup already exists.");
                        Console.Write("Backup encryption password: ");
                        var password = new List<char>();
                        while (true)
                        {
                            var k = Console.ReadKey(true);
                            if (k.Key == ConsoleKey.Enter)
                                break;
                            if (k.Key == ConsoleKey.Backspace)
                            {
                                if (password.Count > 0)
                                    password.RemoveAt(password.Count - 1);
                            }
                            else if (!char.IsControl(k.KeyChar))
                                password.Add(k.KeyChar);
                        }
                        Console.WriteLine();
                        if (password.Count < 12)
                            throw new ArgumentException("Use at least 12 characters.");
                        char[] characters = password.ToArray();
                        password.Clear();
                        using ECDsa key = LoadKey(Arg(args, "--key"), out _);
                        try
                        {
                            File.WriteAllText(output, key.ExportEncryptedPkcs8PrivateKeyPem(characters, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 200000)));
                            RestrictToOwner(output);
                        }
                        finally { Array.Clear(characters); }
                        Console.WriteLine("Encrypted backup saved. Keep it outside client packages.");
                        break;
                    }
                case "restore":
                    {
                        string destination = Arg(args, "--key");
                        if (File.Exists(destination))
                            throw new IOException("Key already exists; restoration will not overwrite it.");
                        var expected = ReadJson(Arg(args, "--public"));
                        char[] password = ReadPassword();
                        using ECDsa key = ECDsa.Create();
                        try
                        {
                            key.ImportFromEncryptedPem(File.ReadAllText(Arg(args, "--backup")), password);
                        }
                        finally { Array.Clear(password); }
                        string id = expected["keyId"]!.GetValue<string>();
                        var actual = PublicKey(key, id);
                        if (actual["x"]!.GetValue<string>() != expected["x"]!.GetValue<string>() || actual["y"]!.GetValue<string>() != expected["y"]!.GetValue<string>())
                            throw new CryptographicException("Backup does not match the trusted public key.");
                        byte[] bytes = key.ExportPkcs8PrivateKey();
                        try
                        {
                            WriteJson(destination, new JsonObject { ["version"] = 1, ["keyId"] = id, ["protectedPkcs8"] = Convert.ToBase64String(WindowsProtection.Protect(bytes)) });
                            RestrictToOwner(destination);
                        }
                        finally { CryptographicOperations.ZeroMemory(bytes); }
                        Console.WriteLine("Existing signing identity restored under the current Windows account.");
                        break;
                    }
                case "test-fixtures":
                    {
                        using ECDsa key = LoadKey(Arg(args, "--key"), out string id);
                        var request = ReadJson(Arg(args, "--request"));
                        string directory = Arg(args, "--out");
                        Directory.CreateDirectory(directory);
                        var baseline = new JsonObject { ["licenseFormatVersion"] = 1, ["keyId"] = id, ["productId"] = "JP-Vision", ["licenseId"] = Guid.NewGuid().ToString(), ["customer"] = "Negative-test-fixture", ["grantType"] = "perpetual", ["machineBinding"] = request["machineBinding"]!.DeepClone(), ["features"] = new JsonArray("studio", "runtime") };
                        void Fixture(string name, Action<JsonObject> change)
                        {
                            var payload = (JsonObject)baseline.DeepClone();
                            change(payload);
                            WriteJson(Path.Combine(directory, name + ".vlic"), Sign(key, payload));
                        }
                        Fixture("wrong-machine", p => p["machineBinding"]!["fingerprint"] = new string('0', 64));
                        Fixture("wrong-product", p => p["productId"] = "OtherProduct");
                        Fixture("unknown-key", p => p["keyId"] = "unknown-fixture-key");
                        Fixture("no-runtime", p => p["features"] = new JsonArray("studio"));
                        Fixture("no-studio", p => p["features"] = new JsonArray("runtime"));
                        Fixture("bad-version", p => p["licenseFormatVersion"] = 999);
                        Console.WriteLine("Signed negative fixtures generated; no private key exported.");
                        break;
                    }
                default:
                    throw new ArgumentException("Unknown command.");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"LicenseIssuer: {ex.Message}"); return 1; }
    }
}

internal static class WindowsProtection
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size; public IntPtr Data;
    }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static byte[] Protect(byte[] bytes) => Transform(bytes, true);
    public static byte[] Unprotect(byte[] bytes) => Transform(bytes, false);
    private static unsafe byte[] Transform(byte[] bytes, bool protect)
    {
        IntPtr memory = Marshal.AllocHGlobal(bytes.Length);
        var input = new Blob { Size = bytes.Length, Data = memory };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            bool ok = protect ? CryptProtectData(ref input, "JP-Vision signing key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                              : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok)
                throw new CryptographicException($"Windows key protection failed: {Marshal.GetLastWin32Error()}");
            byte[] result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(new Span<byte>((void*)memory, bytes.Length));
            Marshal.FreeHGlobal(memory);
            if (output.Data != IntPtr.Zero)
            {
                CryptographicOperations.ZeroMemory(new Span<byte>((void*)output.Data, output.Size));
                LocalFree(output.Data);
            }
        }
    }
}

