namespace EvilIslands.DatabaseEditor.Core;

/// <summary>Checks selected equipment without changing FIG data or rendering bindings.</summary>
public static class FigureTextureValidation
{
    public static string? Weapon(FigureMesh mesh, bool left, string resource) =>
        CheckWeapon(mesh, left, resource)?.Message;

    public static ValidationDiagnostic? CheckWeapon(FigureMesh mesh, bool left, string resource)
    {
        bool quarter = mesh.UV.Length > 0 && mesh.UV.All(v =>
            v.X >= (left ? .5f : 0) && v.X <= (left ? 1 : .5f) && v.Y >= .5f && v.Y <= 1);
        if (quarter && mesh.TextureSlot == 1)
            return new(DiagnosticSeverity.Error, "FIG_WEAPON_TEXTURE_ID", $"Ошибка текстуры оружия: {resource} → {mesh.Name}: TextureID=1 (атлас тела), ожидается 2 (атлас оружия) для этих UV. Предпросмотр соблюдает данные ресурса; исправление автоматически не применяется.", resource + " → " + mesh.Name, Domain: DiagnosticDomain.Resources);
        if (mesh.TextureSlot != 2 || !quarter)
            return new(DiagnosticSeverity.Warning, "FIG_TEXTURE_LAYOUT_UNCHECKED", $"Не проверена схема текстуры оружия: {resource} → {mesh.Name}: TextureID={mesh.TextureSlot}. UV или ID отличаются от поддержанного оружейного атласа; данные сохранены, соответствие игре не подтверждено.", resource + " → " + mesh.Name, Domain: DiagnosticDomain.Resources);
        return null;
    }
}
