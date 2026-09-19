using System.Globalization;
using System.IO;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.UiSmoke;

internal static class PrototypeCombatRegression
{
    internal static void Check()
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        Require(PrototypeCombatCalculation.Overload(.649999).Attack == 0 && PrototypeCombatCalculation.Overload(.65).Attack == -1, "Load attack threshold");
        Require(PrototypeCombatCalculation.Overload(.5).Defense == -1 && PrototypeCombatCalculation.Overload(1.5).Damage == .3, "Load defense/damage thresholds");
        Require(double.IsNaN(PrototypeCombatCalculation.Overload(double.NaN).Attack), "Unknown load becomes zero");
        string folder = Path.Combine(Path.GetTempPath(), "editor-combat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "lua"));
        // Synthetic fixture only; no game data or source copied into the repository.
        File.WriteAllText(Path.Combine(folder, "main.lua"), """
            UnitPerkType = { Melee=2, Archery=3, Strength=13, Lift=14, Armory=18, WeaponsHeavy=19, Dexterity=23, WeaponsLight=29, Intelligence=30, Flow=35, WeaponsMiddle=37, WeaponsMaster=39, Duelist=41, StrongMan=42, DexMan=43 }
            UnitStatType = { StatStrength=3, StatDexterity=4, StatIntelligence=5 }
            ArmorPropType = { Attack=3, Defense=2 }
            WeaponPropType = { WeightType=7 }
            MaterialPropType = { Strength=10, Dexterity=11, Intelligence=12 }
            """);
        string Row(string name, int count, Dictionary<int, double> fields) => "{\"" + name + "\"," + string.Join(",", Enumerable.Range(2, count - 1).Select(i => fields.GetValueOrDefault(i).ToString(CultureInfo.InvariantCulture))) + "}";
        string tables = "UnitPerk={" + Row("test", 44, new() { [2]=80,[3]=40,[13]=1,[14]=1,[18]=2,[29]=1,[41]=1,[43]=1 }) + "}\n"
            + "UnitStat={" + Row("test",21,new(){[3]=50,[4]=25,[5]=25}) + "}\n"
            + "ArmorProp={{\"helm\",-2,-2},{\"helm\",-3,-2}}\n"
            + "WeaponProp={" + Row("sword",7,new(){[7]=1}) + "}\n"
            + "MaterialProp={" + Row("iron",14,new(){[10]=2}) + "}\n";
        File.WriteAllText(Path.Combine(folder, "lua", "tables.lua"), tables);
        File.WriteAllText(Path.Combine(folder, "lua", "npc_consumables.lua"), "NpcCombatConsumables={prototypes={}}");
        ModelRecord Record(string table, string name, Dictionary<int,string> fields, Dictionary<int,string[]>? lists=null) => new(table,name,fields,lists ?? new());
        var unit = Record("units.prototypes","test",new(){[1]="race",[11]="2",[33]="sword.iron"},new(){[32]=["helm.iron"]});
        var records = new List<ModelRecord> { unit,
            Record("units.races","race",new(){[39]="1, 2, 3, 4, 5, 6, 7"}),
            Record("items.armors","helm",new(){[2]="0",[8]="2",[21]="2, 1, 1, 1, 1, 1, 1, 1, 0"}),
            Record("items.materials","iron",new(){[5]="3",[10]="4",[11]="2, 2, 2, 2, 2, 2, 2"}),
            Record("items.weapons","sword",new(){[2]="0",[8]="1",[24]="5",[25]="2",[29]="3",[30]="4"}) };
        for(int i=0;i<96;i++) records.Add(Record("perks.perks","perk"+i,new(){[19]=(i switch {0=>5,3=>10,16=>20,48=>2,_=>0}).ToString(CultureInfo.InvariantCulture)}));
        var lua = LuaDataWorkspace.Open(folder);
        var report = new EquipmentStatReport();
        report.General.AddRange([new("Прототип","test"),new("Раса","race")]);
        PrototypeCombatCalculation.CalculateJmvReference(report,unit,records,lua);
        string Value(string name) => report.General.Single(r=>r.Name==name).Value;
        Require(Value("Сила")=="57", "UnitStat + perk + armor material");
        Require(Value("Оружие 1 · атака")=="83", "Skill + weapon + duplicate armor rows + mastery + mitigation");
        Require(Value("Оружие 1 · защита")=="88", "Defense and duelist");
        // Native NPC attack reset removes material attributes before the Lua damage callback.
        Require(Value("Оружие 1 · урон")=="33.8–47.32", "Additive damage contributions or material applied twice");
        Require(report.Details.First().Rows[0].Value=="2.88", "NPC weighted armor + race + perk");
        Require(PrototypeCombatCalculation.UsesOtherHand(0,1,false) && PrototypeCombatCalculation.UsesOtherHand(0,9,false), "Melee pair/shield resolution");
        Require(!PrototypeCombatCalculation.UsesOtherHand(5,9,true) && !PrototypeCombatCalculation.UsesOtherHand(12,0,false) && PrototypeCombatCalculation.UsesOtherHand(12,0,true), "Two handed/StrongMan resolution");
        Require(PrototypeCombatCalculation.UsesOtherHand(11,11,false) && !PrototypeCombatCalculation.UsesOtherHand(11,0,false) && PrototypeCombatCalculation.UsesOtherHand(11,9,false), "Staff pairing resolution");
        var pairedUnit = unit with { Fields = new Dictionary<int,string>(unit.Fields){[42]="other.iron"} };
        var otherWeapon = Record("items.weapons","other",new(){[2]="0",[8]="1",[24]="5",[25]="2",[29]="7",[30]="8"});
        var dualRecords = records.Append(otherWeapon).ToArray();
        var dual = new EquipmentStatReport(); dual.General.AddRange([new("Прототип","test"),new("Раса","race")]);
        PrototypeCombatCalculation.CalculateJmvReference(dual,pairedUnit,dualRecords,lua);
        Require(dual.General.Single(r=>r.Name=="Оружие 1 · атака").Value=="78" && dual.General.Single(r=>r.Name=="Оружие 1 · защита").Value=="87", "Dual bonuses must average, apply -7 and suppress Duelist");
        var shield = new EquipmentStatReport(); shield.General.AddRange([new("Прототип","test"),new("Раса","race")]);
        dualRecords[^1] = otherWeapon with { Fields = new Dictionary<int,string>(otherWeapon.Fields){[2]="9"} };
        PrototypeCombatCalculation.CalculateJmvReference(shield,pairedUnit,dualRecords,lua);
        Require(shield.General.Single(r=>r.Name=="Оружие 1 · атака").Value=="90" && shield.General.Single(r=>r.Name=="Оружие 1 · защита").Value=="93", "Shield adds bonuses, no averaging or dual penalty");
        Require(!PrototypeCombatCalculation.SupportedFormulas(File.ReadAllText(Path.Combine(folder,"main.lua"))), "Foreign formulas admitted");
        var material = records.Single(r=>r.Table=="items.materials");
        records[records.IndexOf(material)] = material with { Fields = new Dictionary<int,string>(material.Fields){[5]="300"} };
        var heavy = new EquipmentStatReport(); heavy.General.AddRange([new("Прототип","test"),new("Раса","race")]);
        PrototypeCombatCalculation.CalculateJmvReference(heavy,unit,records,lua);
        Require(heavy.General.Single(r=>r.Name=="Оружие 1 · атака").Value=="79", "Draft weight did not recalculate attack");
        Require(heavy.General.Single(r=>r.Name=="Оружие 1 · урон").Value=="28.8–40.32", "Draft weight did not recalculate damage");
        File.WriteAllText(Path.Combine(folder,"lua","tables.lua"), tables.Replace("\"test\",0,50", "\"test\",0,\"bad\"",StringComparison.Ordinal));
        var malformed = new EquipmentStatReport(); malformed.General.AddRange([new("Прототип","test"),new("Раса","race")]);
        PrototypeCombatCalculation.CalculateJmvReference(malformed,unit,records,LuaDataWorkspace.Open(folder));
        Require(malformed.General.Single(r=>r.Name=="Сила").Value=="нет данных", "Malformed Lua silently became zero");
        Require(malformed.General.Single(r=>r.Name=="Оружие 1 · атака").Value=="нет данных", "Unknown capacity yielded plausible attack");
        Console.WriteLine("PASS prototype arithmetic, load thresholds, duplicate Lua rows, native reset order, draft recalculation, unknown inputs");
    }
}
