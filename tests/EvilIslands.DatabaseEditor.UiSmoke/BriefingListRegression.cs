using System.IO;
using System.Security.Cryptography;
using EvilIslands.DatabaseEditor.App.Models;
using EvilIslands.DatabaseEditor.App.Services;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;
internal static class BriefingListRegression
{
    public static int Run(string source, string output)
    {
        try
        {
            output = Path.GetFullPath(output);
            Directory.CreateDirectory(output);
            string db = Path.Combine(output, "database.res"), texts = Path.Combine(output, "texts.res");
            File.Copy(source, db, true);
            File.Copy(Path.Combine(Path.GetDirectoryName(source)!, "texts.res"), texts, true);
            byte[] baseline = SHA256.HashData(File.ReadAllBytes(db));
            var core = DatabaseDocument.Open(db);
            var real = core.Databases.SelectMany(g => g.Tables).Single(t => t.Key == "quests.briefings");
            var realNames = real.Records.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var service = new CoreDatabaseWorkspaceService();
            var snapshot = service.OpenAsync(db, default).GetAwaiter().GetResult();
            DatabaseTableViewModel Table(WorkspaceSnapshot s) => s.Databases.SelectMany(g => g.Tables).Single(t => t.Key == "quests.briefings");
            void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
            var table = Table(snapshot);
            var expected = snapshot.TextResources.Where(t => t.Prefix.Equals("BRIEFING", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.ResourceName[(t.Prefix.Length + 1)..].Trim()).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            expected.UnionWith(realNames);
            Check(table.RecordCount == expected.Count + real.RecordCount - realNames.Count, $"Union count or duplicate error: real={real.RecordCount}, names={realNames.Count}, expected={expected.Count}, actual={table.RecordCount}, text={snapshot.TextResources.Count}");
            Check(table.Records.Select(r => r.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expected), "Missing briefing");
            Check(service.PrepareModuleSave().Count == 0, "Opening dirtied a document");
            var detached = table.Records.First(r => r.IsTextOnlyBriefing);
            Check(detached.CanEditPrimaryLocalization && detached.CoreRecord.Fields.All(f => !f.IsEditable), "Reference edit policy");
            bool blocked = false;
            try { service.AppendRecordClone(table, detached); } catch (InvalidOperationException) { blocked = true; }
            Check(blocked, "Detached row cloned into DB");
            string name = detached.DisplayName;
            detached.LocalizationDescription += "\r\nRegression text";
            Check(!detached.PrimaryLocalization!.HasError, "Text editing failed");
            Check(service.PrepareModuleSave().Count == 1 && service.PrepareModuleSave()[0].Path == texts, "Unexpected save targets");
            string changed = detached.LocalizationDescription;
            service.RefreshRelatedSounds(detached);
            var saved = service.SaveCurrentAsync(false, default).GetAwaiter().GetResult();
            var reopened = Table(saved.Workspace);
            Check(reopened.RecordCount == expected.Count + real.RecordCount - realNames.Count, "Duplicate on reload");
            Check(reopened.Records.Single(r => r.DisplayName == name).LocalizationDescription == changed, "Text save/reopen lost edit");
            Check(SHA256.HashData(File.ReadAllBytes(db)).SequenceEqual(baseline), "DB changed");
            Check(DatabaseDocument.Open(db).Databases.SelectMany(g => g.Tables).Single(t => t.Key == "quests.briefings").RecordCount == real.RecordCount, "DB row inserted");
            foreach (var row in reopened.Records.Where(r => r.IsTextOnlyBriefing)) Console.WriteLine($"TEXT ONLY {row.DisplayName} phrases={row.BriefingPhrases.Count}");
            Console.WriteLine($"PASS: QDB={real.RecordCount}; union={reopened.RecordCount}; case-insensitive dedup; readonly references; text save/reopen; DB unchanged.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
