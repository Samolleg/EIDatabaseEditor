using System.Text.Json;
namespace EvilIslands.DatabaseEditor.Core;

public sealed record MmpBatchItem(string Name, bool Success, string Detail);
public sealed record MmpBatchProgress(int Completed, int Total, string Name);
public static class MmpBatchConverter
{
    public static string MapResourceName(string name)
    {
        if (!name.EndsWith(".mmp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ресурс не является MMP.");
        if (name.EndsWith("map.mmp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ресурс уже имеет суффикс map; повторная конвертация пропущена.");
        return name[..^4] + "map.mmp";
    }
    public static async Task<IReadOnlyList<MmpBatchItem>> RunAsync(ResFileManagerArchive archive,
        IReadOnlyList<string> names, string destination, bool asRes, MmpDxtChoice choice, string compressor,
        IProgress<MmpBatchProgress>? progress, CancellationToken token)
    {
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Выберите новую папку результата.");
        string parent = Path.GetDirectoryName(destination)!;
        string stage = Path.Combine(parent, ".mmp-converting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var report = new List<MmpBatchItem>();
        try
        {
            // Hold a verified read handle throughout the batch: one hash, no concurrent source writes.
            using var source = await Task.Run(() => archive.OpenVerifiedSource(token), token);
            var entries = archive.Entries.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
            string files = Path.Combine(stage,"MMP"); Directory.CreateDirectory(files);
            var imports = new List<ResFileEdit>();
            foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new(report.Count,names.Count,name));
                string? target = null;
                string scratch = Path.Combine(stage, "scratch-"+Guid.NewGuid().ToString("N"));
                try
                {
                    string outputName = MapResourceName(name);
                    if (archive.Contains(outputName)) throw new InvalidDataException($"Ресурс {outputName} уже существует; замена не выполняется.");
                    string candidate = Path.GetFullPath(outputName.Replace('\\',Path.DirectorySeparatorChar),files);
                    if (!candidate.StartsWith(files+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Небезопасный путь ресурса.");
                    target = candidate;
                    if (!entries.TryGetValue(name, out var entry)) throw new KeyNotFoundException("Ресурс не найден.");
                    if (entry.Size > MmpPreviewDecoder.MaximumInputBytes) throw new InvalidDataException("Ресурс превышает лимит 128 МиБ.");
                    source.Position = entry.Offset;
                    byte[] data = new byte[checked((int)entry.Size)];
                    await source.ReadExactlyAsync(data, token);
                    MmpImage image = await Task.Run(() => MmpPreviewDecoder.Decode(data, token), token);
                    MmpConversionResult converted = await Task.Run(()=>MmpDxtConverter.ConvertAsync(image,choice,compressor,scratch,token),token);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await File.WriteAllBytesAsync(target,converted.Bytes,token);
                    imports.Add(new(ResFileEditKind.AddOrReplace,outputName,await ResFileManagerArchive.InspectImportAsync(target,token)));
                    report.Add(new(name,true,$"{outputName}; {(choice == MmpDxtChoice.MapAtlasX2 ? image.Width * 2 : image.Width)}×{(choice == MmpDxtChoice.MapAtlasX2 ? image.Height * 2 : image.Height)}; {image.Format} → {converted.Format}; mip: {converted.Mips}" +
                        (choice == MmpDxtChoice.MapAtlasX2 ? "; атлас 2×2, четыре копии без масштабирования; alpha threshold 128/255" : "") +
                        (converted.HadSmoothAlpha && choice is MmpDxtChoice.MapAtlasX2 or MmpDxtChoice.Dxt1Threshold ? "; полупрозрачные пиксели преобразованы в однобитную alpha" : "") +
                        (converted.Layer ? "; PNT3 — отдельный слой, композиция и UV не проверены" : "")));
                }
                catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or NotSupportedException or KeyNotFoundException)
                { if (target is not null && File.Exists(target)) File.Delete(target); report.Add(new(name,false,e.Message)); }
                finally { if (Directory.Exists(scratch)) Directory.Delete(scratch,true); }
            }
            token.ThrowIfCancellationRequested();
            if (asRes && imports.Count > 0)
            {
                var exportEdits = archive.Entries.Where(entry => !imports.Any(item => item.ResourceName.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))).Select(entry => new ResFileEdit(ResFileEditKind.Delete, entry.Name)).ToList();
                exportEdits.AddRange(imports);
                await archive.SaveCopyAsync(exportEdits,Path.Combine(stage,"converted.res"),token);
                var reopened = await ResFileManagerArchive.OpenAsync(Path.Combine(stage,"converted.res"),token);
                if (reopened.Entries.Count!=imports.Count) throw new InvalidDataException("Не совпадает число ресурсов результата.");
                Directory.Delete(files,true);
            }
            await File.WriteAllTextAsync(Path.Combine(stage,"report.json"),JsonSerializer.Serialize(new { Completed=true,
                Successful=report.Count(r=>r.Success), Failed=report.Count(r=>!r.Success), Files=report },new JsonSerializerOptions { WriteIndented=true }),token);
            await File.WriteAllTextAsync(Path.Combine(stage,"README.txt"),"PNT3 может быть отдельным слоем. Конвертация не собирает доспех и не меняет UV. Проверка в игре не выполнена. Ресурсы для объектов подключаются отдельно после проверки группы загрузки. Ошибки и пропуски: report.json.",token);
            token.ThrowIfCancellationRequested();
            Directory.Move(stage,destination);
            progress?.Report(new(report.Count,names.Count,"Готово"));
            return report;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage,true); }
    }
}
