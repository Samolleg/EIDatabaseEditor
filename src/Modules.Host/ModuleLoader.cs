using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using DatabaseEditor.Modules.V1;
using DatabaseEditor.Modules.Wpf.V1;

namespace DatabaseEditor.Modules.Host;

public sealed record ModuleManifest(string ModuleId, string Version, string EntryAssembly, string EntryType,
    int ContractMajor, int ContractMinor, string[] RequiredCapabilities, string[] OptionalCapabilities,
    string Runtime, string Architecture, int StateFormatVersion);
public sealed record LoadedModule(ModuleManifest Manifest, IWpfEditorModule Instance);
public sealed record ModuleLoadResult(IReadOnlyList<LoadedModule> Modules, IReadOnlyList<string> Errors);

public static class ModuleLoader
{
    public static ModuleLoadResult Load(string directory, IModuleHost host)
    {
        var loaded = new List<LoadedModule>(); var errors = new List<string>();
        if (!Directory.Exists(directory)) return new(loaded, errors);
        string[] manifests;
        try
        {
            for (DirectoryInfo? parent = new(directory); parent is not null; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Папка модулей проходит через символическую ссылку.");
            manifests = Directory.GetFiles(directory, "module.json", new EnumerationOptions
            { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { errors.Add("Модули недоступны: " + ex.Message); return new(loaded, errors); }
        var candidates = new List<(string Path, ModuleManifest Manifest)>();
        foreach (string path in manifests)
        {
            try
            {
                if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Манифест слишком большой.");
                var m = JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException("Пустой манифест.");
                if (string.IsNullOrWhiteSpace(m.ModuleId) || !System.Text.RegularExpressions.Regex.IsMatch(m.ModuleId, "^[a-z0-9][a-z0-9.-]{0,63}$"))
                    throw new InvalidDataException("Недопустимый ID модуля.");
                if (m.ContractMajor != 1 || m.ContractMinor != 0 || m.Runtime != "net10.0-windows" ||
                    (m.Architecture != "any" && !m.Architecture.Equals(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Несовместимый контракт или платформа.");
                if (m.RequiredCapabilities is null || m.RequiredCapabilities.Except(host.Capabilities).Any())
                    throw new InvalidDataException("Хост не поддерживает обязательные возможности.");
                SafePath(Path.GetDirectoryName(path)!, m.EntryAssembly);
                candidates.Add((path, m));
            }
            catch (Exception ex) { errors.Add(Path.GetFileName(Path.GetDirectoryName(path)) + ": " + ex.Message); }
        }
        foreach (var c in candidates)
        {
            IWpfEditorModule? module = null;
            try
            {
                if (candidates.Count(x => x.Manifest.ModuleId == c.Manifest.ModuleId) != 1)
                    throw new InvalidDataException("Повторный ID модуля: " + c.Manifest.ModuleId);
                string entry = SafePath(Path.GetDirectoryName(c.Path)!, c.Manifest.EntryAssembly);
                var context = new ModuleContext(entry);
                var type = context.LoadFromAssemblyPath(entry).GetType(c.Manifest.EntryType, true)!;
                module = Activator.CreateInstance(type) as IWpfEditorModule ?? throw new InvalidDataException("Тип не реализует WPF контракт.");
                module.Initialize(host);
                loaded.Add(new(c.Manifest, module));
            }
            catch (Exception ex)
            {
                try { module?.Dispose(); } catch { /* Preserve original initialization failure. */ }
                errors.Add(c.Manifest.ModuleId + ": " + ex.GetBaseException().Message);
            }
        }
        return new(loaded, errors);
    }

    internal static string SafePath(string root, string relative)
    {
        root = Path.GetFullPath(root);
        string path = Path.GetFullPath(relative, root);
        if (Path.IsPathRooted(relative) || !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Путь сборки выходит за каталог модуля.");
        for (var f = new FileInfo(path) as FileSystemInfo; f is not null; f = f is FileInfo file ? file.Directory : ((DirectoryInfo)f).Parent)
        {
            if ((f.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Ссылки в пути модуля не поддерживаются.");
            if (f.FullName.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return path;
    }

    private sealed class ModuleContext(string entry) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver resolver = new(entry);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is "ModuleContracts.V1" or "ModuleContracts.Wpf.V1")
                return Default.LoadFromAssemblyName(name);
            string? path = resolver.ResolveAssemblyToPath(name);
            if (path is null) return null; // WPF and framework types are supplied by the host runtime.
            return LoadFromAssemblyPath(SafePath(Path.GetDirectoryName(entry)!, Path.GetRelativePath(Path.GetDirectoryName(entry)!, path)));
        }
        protected override IntPtr LoadUnmanagedDll(string name)
        {
            string? path = resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(SafePath(Path.GetDirectoryName(entry)!, Path.GetRelativePath(Path.GetDirectoryName(entry)!, path)));
        }
    }
}
