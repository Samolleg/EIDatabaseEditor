namespace EvilIslands.DatabaseEditor.Verification;

internal sealed record KnownDatabaseBaseline(
    string Name,
    string Sha256,
    int MaterialCount,
    int TotalDiagnostics,
    int MissingBaseRaceReferences,
    bool HasMissingBriefingRune,
    int ItemFormat,
    int SpellFormat,
    int MissingEmbeddedRunes,
    int TooManyRunes,
    int EnergyExceeded,
    int ComplexityExceeded,
    int MaterialMismatch,
    int MissingMaterial,
    int MissingItemRune,
    int MagicRelated)
{
    private static readonly IReadOnlyDictionary<string, KnownDatabaseBaseline> BySha256 =
        new[]
        {
            new KnownDatabaseBaseline(
                "JamaisVu135 2026-08-22",
                "E77E3E9E94A110C61AC858AC47A4F9D982CEBCDA843897915E24B7041FF0800A",
                51,
                176,
                0,
                true,
                2,
                2,
                2,
                3,
                10,
                155,
                1,
                0,
                0,
                175),
            new KnownDatabaseBaseline(
                "DBEditor no-op reference 2026-08-01",
                "10CC977D89676F8A072A5C9A20C6C5B45DA9301D6653D35481F2F61D486E2BE2",
                51,
                180,
                4,
                true,
                2,
                2,
                2,
                3,
                10,
                155,
                1,
                0,
                0,
                175),
        }.ToDictionary(item => item.Sha256, StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string? sha256, out KnownDatabaseBaseline? baseline)
    {
        if (!string.IsNullOrWhiteSpace(sha256) && BySha256.TryGetValue(sha256, out KnownDatabaseBaseline? found))
        {
            baseline = found;
            return true;
        }

        baseline = null;
        return false;
    }
}
