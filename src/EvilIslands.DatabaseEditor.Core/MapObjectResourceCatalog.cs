using System.Numerics;

namespace EvilIslands.DatabaseEditor.Core;

public sealed record MapObjectResource(string Model, string ArchivePath, string ModResource, string? BonResource)
{
    public string? BonArchivePath { get; init; }
    public string ArchiveName => Path.GetFileName(ArchivePath);
    public string Label => $"{Model} — {ArchiveName}";
}

/// <summary>Read-only catalogue of model resources beside the open database.</summary>
public static class MapObjectResourceCatalog
{
    public static Task<IReadOnlyList<MapObjectResource>> DiscoverAsync(string databasePath, CancellationToken token = default) =>
        Task.Run<IReadOnlyList<MapObjectResource>>(() =>
        {
            var sources=TextureSources.FromDatabaseFolderAsync(databasePath,token).GetAwaiter().GetResult();
            var resources=new Dictionary<string,(string Owner,string Name)>(StringComparer.Ordinal);
            var claimed=new HashSet<string>(StringComparer.Ordinal);
            if(sources.ConfigurationPath is { } config){
                string root=Path.GetDirectoryName(config)!;
                foreach(string file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)){
                    string name=Path.GetRelativePath(root,file);if(!name.EndsWith(".mod",StringComparison.OrdinalIgnoreCase)&&!name.EndsWith(".bon",StringComparison.OrdinalIgnoreCase))continue;
                    if(DatabaseSafetyPolicy.ContainsReparsePoint(file))continue;
                    string key=PakArchive.LookupKey(name);if(claimed.Add(key))resources.Add(key,(root,name));
                }
            }
            foreach(string archive in UnitModelPreview.FigureArchives(databasePath,sources).Where(File.Exists)){
                token.ThrowIfCancellationRequested();
                using var pak=PakArchive.IsPak(archive)?new ReadOnlyPakArchive(archive,token):null;
                if(pak is not null)foreach(string name in pak.Deleted)claimed.Add(PakArchive.LookupKey(name));
                foreach(var entry in pak?.Entries??ResFileManagerArchive.ReadCatalog(archive,token)){
                    string key=PakArchive.LookupKey(entry.Name);if(claimed.Add(key))resources.Add(key,(archive,entry.Name));
                }
            }
            return resources.Values.Where(r=>r.Name.EndsWith(".mod",StringComparison.OrdinalIgnoreCase)).Select(r=>{
                string model=r.Name[..^4];bool found=resources.TryGetValue(PakArchive.LookupKey(model+".bon"),out var bon);
                return new MapObjectResource(model,r.Owner,r.Name,found?bon.Name:null){BonArchivePath=found?bon.Owner:null};
            }).OrderBy(r=>r.Model,StringComparer.OrdinalIgnoreCase).ThenBy(r=>r.ArchiveName,StringComparer.OrdinalIgnoreCase).ToArray();
        }, token);

    public static Task<UnitModelScene> LoadAsync(MapObjectResource resource, CancellationToken token = default) =>
        LoadAsync(resource, new Vector3(.5f), token);

    /// <summary>Mapper complexion: X strength, Y dexterity, Z height; each is in [0, 1].</summary>
    public static Task<UnitModelScene> LoadAsync(MapObjectResource resource, Vector3 morph, CancellationToken token = default) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            if (!float.IsFinite(morph.X) || !float.IsFinite(morph.Y) || !float.IsFinite(morph.Z) ||
                morph.X < 0 || morph.X > 1 || morph.Y < 0 || morph.Y > 1 || morph.Z < 0 || morph.Z > 1)
                throw new ArgumentOutOfRangeException(nameof(morph), "Параметры формы должны быть от 0 до 1.");
            // The mapper interpolates dexterity inside each sample pair, then strength
            // between pairs. The shared FIG decoder uses the opposite axis order.
            Vector3 figureMorph = new(morph.Y, morph.X, morph.Z);
            byte[] OwnerBytes(string owner,string name) => Directory.Exists(owner)?File.ReadAllBytes(Path.Combine(owner,name)):ResFileManagerArchive.ReadResource(owner,name,token);
            var mod = ResArchiveDocument.Parse(resource.ModResource,OwnerBytes(resource.ArchivePath,resource.ModResource),token);
            ResArchiveDocument? bon = resource.BonResource is null ? null :
                ResArchiveDocument.Parse(resource.BonResource,OwnerBytes(resource.BonArchivePath??resource.ArchivePath,resource.BonResource),token);
            var parents = UnitModelPreview.ReadHierarchy(mod.GetResourceBytes(resource.Model).ToArray());
            var positions = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Vector3 Position(string part)
            {
                token.ThrowIfCancellationRequested();
                if (positions.TryGetValue(part, out var cached)) return cached;
                if (visiting.Count >= 128 || !visiting.Add(part)) throw new InvalidDataException("Цикл или слишком глубокая иерархия модели.");
                if (!parents.TryGetValue(part, out string? parent)) throw new InvalidDataException("Нет родителя детали: " + part);
                Vector3 offset = bon is not null && bon.TryGetEntry(part, out var bone) && bone is not null
                    ? FigurePreviewDecoder.Bone(bon.GetResourceBytes(bone).ToArray(), figureMorph) : Vector3.Zero;
                var position = offset + (parent.Length == 0 ? Vector3.Zero : Position(parent));
                visiting.Remove(part); positions.Add(part, position); return position;
            }
            var meshes = new List<FigureMesh>();
            foreach (string part in parents.Keys)
            {
                token.ThrowIfCancellationRequested();
                if (!mod.TryGetEntry(part, out var entry) || entry is null) continue;
                var mesh = FigurePreviewDecoder.Decode(part, mod.GetResourceBytes(entry).ToArray(), figureMorph);
                Vector3 offset = Position(part);
                for (int i = 0; i < mesh.Positions.Length; i++) mesh.Positions[i] += offset;
                if (mesh.Indices.Length > 0) meshes.Add(mesh);
            }
            if (meshes.Count == 0) throw new InvalidDataException("Модель не содержит доступной геометрии.");
            string[] notes = ["Модель из RES; расположение экземпляров на карте здесь не показано.",
                "Форма берётся из FIG/BON. Текстуры назначаются для предпросмотра и не записываются в карту."];
            return new UnitModelScene(resource.Model, meshes, new Dictionary<int, MmpImage>(),
                [resource.ModResource + " — " + resource.ArchivePath,
                 resource.BonResource is null ? "BON отсутствует" : resource.BonResource + " — " + resource.ArchivePath], notes);
        }, token);

    public static async Task<UnitModelScene> WithTexturesAsync(UnitModelScene geometry, TextureResourceCatalog catalog,
        string? primary, string? secondary, CancellationToken token = default)
    {
        var textures = new Dictionary<int, MmpImage>();
        var sources = geometry.Sources.ToList();
        foreach (var (slot, name) in new[] { (1, primary), (2, secondary) })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            string resourceName = name.EndsWith(".mmp", StringComparison.OrdinalIgnoreCase) ? name : name + ".mmp";
            var image = await catalog.ReadPreviewAsync(resourceName, token).ConfigureAwait(false);
            // The mapper uses the secondary material only for FIG slot 2;
            // all other slot numbers (for example 8 on map objects) use primary.
            foreach (int figureSlot in geometry.Meshes.Select(mesh => mesh.TextureSlot).Distinct()
                         .Where(figureSlot => (figureSlot == 2 ? 2 : 1) == slot))
                textures[figureSlot] = image;
            sources.Add($"Текстура {slot}: {resourceName} — {catalog.Check(resourceName).Source}");
        }
        return geometry with { Textures = textures, Sources = sources };
    }

    /// <summary>Preview-only uniform scale, also usable with non-morphable FIG1 models.</summary>
    public static UnitModelScene Scale(UnitModelScene scene, float factor)
    {
        if (!float.IsFinite(factor) || factor < .25f || factor > 3f)
            throw new ArgumentOutOfRangeException(nameof(factor), "Масштаб должен быть от 25% до 300%.");
        if (factor == 1f) return scene;
        var meshes = scene.Meshes.Select(mesh => mesh with
        {
            Positions = mesh.Positions.Select(position => position * factor).ToArray()
        }).ToArray();
        return scene with { Meshes = meshes };
    }
}
