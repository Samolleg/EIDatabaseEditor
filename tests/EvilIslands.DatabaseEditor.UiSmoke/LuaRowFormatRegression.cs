using System.IO;
using System.Reflection;
using EvilIslands.DatabaseEditor.App;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class LuaRowFormatRegression
{
    public static int Run(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            var setter = typeof(MainWindow).GetMethod("SetLiteral", BindingFlags.NonPublic | BindingFlags.Static)!;
            string Set(string row, int index, string value) => (string)setter.Invoke(null, [row, new[] { "#" + index }, value])!;
            void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
            string row = "{\"FixtureMonster\"}";
            int[] order = [2, 3, 4, 5, 6, 28, ..Enumerable.Range(8, 20), ..Enumerable.Range(29, 16)];
            foreach (int index in order)
            {
                row = Set(row, index, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Check(!row.Contains('\n'), "Repeated entry made multiline row");
                var parsed = LuaLiteralReader.Read(row);
                Check(parsed.Field("#" + index)!.Value.Text == index.ToString(), "Index shifted");
                Check(parsed.Field("#7") is null, "Missing field was filled");
            }
            Check(row.Contains("[8] = 8") && row.Contains("[28] = 28"), "Sparse indices lost");
            string sparse = row;
            row = Set(row, 7, "7");
            Check(!row.Contains('['), "Filled dense row still has explicit keys");
            Check(LuaLiteralReader.Read(row).Fields.Count == 44, "Full row truncated");
            for (int i = 2; i <= 44; i++) Check(LuaLiteralReader.Read(row).Field("#" + i)!.Value.Text == i.ToString(), "Dense roundtrip shifted");
            string existing = "{\"Old\",0,  80,0}";
            Check(Set(existing, 3, "81") == "{\"Old\",0,  81,0}", "Existing formatting changed");
            foreach (string protectedRow in new[] { "{\"M\", -- keep\r\n[2] = 3}", "{\"M\", [2]=3, [2]=4}", "{name=\"M\", [2]=3}", "{\"M\", {1,2}}" })
                Check(LuaRowFormatting.Compact(protectedRow) == protectedRow, "Protected row rewritten");
            string literal = "{\"M\",[3]=false,[2]=nil,[4]=\"a,b\"}";
            Check(LuaRowFormatting.Compact(literal) == "{\"M\", nil, false, \"a,b\"}", "Literal values changed");
            string legacy = "{\"M\",\r\n[3]=3,\r\n\r\n[2]=2,\r\n}";
            Check(Set(legacy, 4, "4") == "{\"M\", 2, 3, 4}", "Legacy insertion not compacted");
            foreach (string newline in new[] { "\n", "\r\n" })
            {
                string path = Path.Combine(output, newline.Length == 1 ? "lf.lua" : "crlf.lua");
                string baseline = "-- untouched" + newline + "UnitPerk = {" + newline + "    " + existing + "," + newline + "}" + newline;
                File.WriteAllText(path, baseline);
                var doc = LuaSourceDocument.Open(path, "UnitPerk");
                doc.Apply(doc.Append(doc.Tables["UnitPerk"].Value!, row), "new row");
                string changed = doc.Text;
                Check(changed.Contains(existing), "Other row altered");
                doc.Undo(); Check(doc.Text == baseline, "Undo differs");
                doc.Redo(); Check(doc.Text == changed, "Redo differs");
                string backup = doc.Save();
                Check(File.ReadAllText(backup) == baseline, "Backup differs");
                Check(LuaSourceDocument.Open(path, "UnitPerk").Text == changed, "Save/reopen differs");
            }
            File.WriteAllText(Path.Combine(output, "rows.txt"), sparse + "\n" + row);
            string folder = Path.Combine(output, "completeness");
            Directory.CreateDirectory(Path.Combine(folder, "lua"));
            File.WriteAllText(Path.Combine(folder, "main.lua"), "UnitStatType={Name=1,Exp=2,StatStrength=3,StatDexterity=4,StatIntelligence=5,CriticalChance=6,CriticalDamage=7,Encumbrance=8}");
            string tablesPath = Path.Combine(folder, "lua", "tables.lua");
            const string initial = "UnitStat={}";
            File.WriteAllText(tablesPath, initial);
            var workspace = LuaDataWorkspace.Open(folder);
            var statsDoc = workspace.Documents.Single();
            statsDoc.Apply(statsDoc.Append(statsDoc.Tables["UnitStat"].Value!, "{\"Fixture\",292915,26.0,37.0,0.0}"), "partial row");
            bool blocked = false;
            try { workspace.Save(statsDoc); }
            catch (InvalidDataException ex) { blocked = ex.Message.Contains("Fixture") && ex.Message.Contains("[6]") && ex.Message.Contains("[7]"); }
            Check(blocked && File.ReadAllText(tablesPath) == initial && Directory.GetFiles(Path.GetDirectoryName(tablesPath)!).Length == 1, "Partial save wrote file/backup or lacked actionable error");
            var statsRow = statsDoc.Tables["UnitStat"].Value!.Fields.Single().Value;
            string complete = Set(Set(statsDoc.Text[statsRow.Start..statsRow.End], 6, "0"), 7, "0");
            statsDoc.Apply([new LuaPatch(statsRow.Start, statsRow.End - statsRow.Start, complete)], "explicit critical values");
            workspace.Save(statsDoc);
            Check(File.ReadAllText(tablesPath).Contains(complete), "Complete row not saved");
            // Optional set-item enum values are not required in UnitStat rows.
            Check(LuaLiteralReader.Read(complete).Field("#8") is null, "Optional field filled");
            statsRow = statsDoc.Tables["UnitStat"].Value!.Fields.Single().Value;
            var chance = statsRow.Field("#6")!.Value;
            statsDoc.Apply([new LuaPatch(chance.Start, chance.End - chance.Start, "nil")], "clear chance");
            blocked = false;
            try { workspace.ValidateForSave(statsDoc); } catch (InvalidDataException) { blocked = true; }
            Check(blocked, "Explicit nil accepted");
            Console.WriteLine("PASS Lua row format: 43 fields, arbitrary order, gaps, dense conversion, protected rows, existing edits, LF/CRLF save/backup/Undo/Redo");
            Console.WriteLine("PASS UnitStat completeness: partial/nil blocked without writes, named missing fields, explicit zero saved, optional fields retained absent");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
