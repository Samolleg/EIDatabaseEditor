using System.Globalization;
using System.Security;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.Verification;

internal static class SafetyRegressionVerifier
{
    public static async Task RunAsync(
        string sourcePath,
        IReadOnlyCollection<string> allSourcePaths,
        SafeStagingArea staging,
        string sourceSha256,
        VerificationLog log)
    {
        await VerifyChangedStagingSourceIsRejectedAsync(
            sourcePath,
            allSourcePaths,
            staging,
            log).ConfigureAwait(false);
        await VerifySyntheticGamePathIsRejectedAsync(
            sourcePath,
            allSourcePaths,
            staging,
            log).ConfigureAwait(false);
        VerifyCorrectedSchemaMetadata(sourcePath, log);
        VerifyAllPhysicalEditorsAcceptCurrentText(sourcePath, log);
        VerifyAllTablesCanAppendAndUndo(sourcePath, log);
        KnownDatabaseBaseline.TryGet(sourceSha256, out KnownDatabaseBaseline? baseline);
        VerifySemanticDiagnostics(sourcePath, baseline, log);
        VerifyInvalidInputsDoNotModifyDocument(sourcePath, log);
        await VerifyByteStateAndResetAsync(
            sourcePath,
            allSourcePaths,
            staging,
            log).ConfigureAwait(false);
        await VerifyDynamicNameAndResetAsync(
            sourcePath,
            allSourcePaths,
            staging,
            log).ConfigureAwait(false);
        await VerifyJunctionPathIsRejectedOrSkippedAsync(
            sourcePath,
            allSourcePaths,
            staging,
            log).ConfigureAwait(false);
    }

    private static void VerifyCorrectedSchemaMetadata(string sourcePath, VerificationLog log)
    {
        DatabaseDocument document = DatabaseDocument.Open(sourcePath);
        DatabaseField hitLocationType = document.Units.HitLocationTypes[0].Fields.Single(field => field.Id == 4);
        DatabaseField raceType = document.Units.Races[0].Fields.Single(field => field.Id == 1);
        DatabaseField firstStep = document.Units.Races[0].Fields.Single(field => field.Id == 45);
        DatabaseField monsterLevel = document.Units.Prototypes[0].Fields.Single(field => field.Id == 41);
        DatabaseField npcVoice = document.Units.Npcs[0].Fields.Single(field => field.Id == 14);
        log.Assert(
            hitLocationType.Kind == "UInt32" &&
            raceType.Kind == "UInt32" &&
            firstStep.Kind == "Byte" &&
            monsterLevel.Kind == "Int32" &&
            npcVoice.Kind == "Byte",
            "Типы units.udb соответствуют подтверждённым native-структурам.");

        DatabaseRecord powerHit = FindPowerHit(document);
        DatabaseField trace = powerHit.Fields.Single(field => field.Id == 15);
        DatabaseField builtIn = powerHit.Fields.Single(field => field.Id == 16);
        DatabaseField special = powerHit.Fields.Single(field => field.Id == 17);
        DatabaseField shops = powerHit.Fields.Single(field => field.Id == 22);
        DatabaseField red = powerHit.Fields.Single(field => field.Id == 23);
        DatabaseField green = powerHit.Fields.Single(field => field.Id == 24);
        DatabaseField blue = powerHit.Fields.Single(field => field.Id == 25);
        DatabaseField radius = powerHit.Fields.Single(field => field.Id == 26);
        DatabaseField fadeout = powerHit.Fields.Single(field => field.Id == 27);
        log.Assert(
            trace.Kind == "UInt32" &&
            builtIn.Kind == "FixedStringList" && builtIn.IsEditable &&
            special.Kind == "FixedStringList" && special.IsEditable &&
            shops.Kind == "UInt32" && shops.Projections.Count == 5 && shops.IsEditable,
            "Списки кодов и shop-маска spells.sdb декодируются и имеют редактор исходного поля.");
        log.Assert(
            red.DisplayName == "Свет: красный" &&
            green.DisplayName == "Свет: зелёный" &&
            blue.DisplayName == "Свет: синий" &&
            radius.Key == "LightRadius" && radius.DisplayName == "Радиус света" &&
            fadeout.Key == "FadeoutTime" && fadeout.DisplayName == "Время затухания",
            "Поля освещения 23–27 имеют правильные подписи и ключи без сдвига.");
        log.Assert(
            string.Equals(powerHit.DisplayName, FindField(powerHit, "Name").Value.ToString(), StringComparison.Ordinal) &&
            !string.Equals(powerHit.DisplayName, powerHit.GetString("CodeName"), StringComparison.Ordinal),
            "Таблица заклинаний показывает Name отдельно от технического CodeName.");
    }

    private static void VerifyAllPhysicalEditorsAcceptCurrentText(string sourcePath, VerificationLog log)
    {
        DatabaseDocument document = DatabaseDocument.Open(sourcePath);
        DatabaseField[] fields = document.Databases
            .SelectMany(group => group.Tables)
            .SelectMany(table => table.Records)
            .SelectMany(record => record.Fields)
            .ToArray();
        var rejected = new List<string>();
        foreach (DatabaseField field in fields)
        {
            string? error = null;
            if (!field.IsEditable || !field.TrySetValue(field.EditorText, out error))
            {
                rejected.Add($"{field.Path}: {error ?? "редактор недоступен"}");
            }
        }

        log.Assert(
            rejected.Count == 0 && !document.IsModified,
            $"Все {fields.Length:N0} физических полей принимают собственное отображаемое значение без изменения байтов" +
            (rejected.Count == 0 ? "." : $"; первый отказ: {rejected[0]}"));
    }

    private static void VerifyAllTablesCanAppendAndUndo(string sourcePath, VerificationLog log)
    {
        DatabaseDocument document = DatabaseDocument.Open(sourcePath);
        DatabaseTable[] tables = document.Databases.SelectMany(group => group.Tables).ToArray();
        int[] counts = tables.Select(table => table.RecordCount).ToArray();
        foreach (DatabaseTable table in tables)
        {
            DatabaseRecord added = document.AppendRecordClone(table.Records[0]);
            document.RemoveAddedRecord(added);
        }

        log.Assert(
            !document.IsModified && tables.Select(table => table.RecordCount).SequenceEqual(counts),
            "Во всех 26 таблицах клон строки добавляется в конец и полностью отменяется без изменения документа.");
    }

    private static void VerifySemanticDiagnostics(
        string sourcePath,
        KnownDatabaseBaseline? baseline,
        VerificationLog log)
    {
        DatabaseDocument document = DatabaseDocument.Open(sourcePath);
        IReadOnlyList<ValidationDiagnostic> first = document.Validate();
        IReadOnlyList<ValidationDiagnostic> second = document.Validate();
        log.Assert(
            first.SequenceEqual(second),
            "Повторная семантическая проверка детерминирована.");
        log.Assert(
            first.Where(diagnostic => diagnostic.Domain == DiagnosticDomain.Semantic)
                .All(diagnostic => !diagnostic.IsBlocking && diagnostic.Location is not null),
            "Семантические сообщения имеют точное место и не блокируют безопасное сохранение копии.");
        if (baseline is not null)
        {
            int missingBaseRace = first.Count(diagnostic =>
                diagnostic.Domain == DiagnosticDomain.Semantic &&
                diagnostic.Code == "SEM_REFERENCE_NOT_FOUND" &&
                diagnostic.Location?.TableKey == "units.prototypes" &&
                diagnostic.Location.FieldId == 1);
            log.Assert(
                missingBaseRace == baseline.MissingBaseRaceReferences,
                $"Контент-база «{baseline.Name}» имеет ровно " +
                $"{baseline.MissingBaseRaceReferences} отсутствующих ссылок Base Race.");

            bool hasMissingBriefingRune = first.Any(diagnostic =>
                diagnostic.Domain == DiagnosticDomain.Semantic &&
                diagnostic.Code == "SEM_REFERENCE_NOT_FOUND" &&
                diagnostic.Location?.TableKey == "quests.briefings" &&
                diagnostic.Location.RecordIndex == 187 &&
                diagnostic.Location.FieldId == 3);
            log.Assert(
                hasMissingBriefingRune == baseline.HasMissingBriefingRune,
                baseline.HasMissingBriefingRune
                    ? "В контент-базе локализована ссылка briefing Give Items на отсутствующую rune.t3."
                    : "В контент-базе отсутствует прежняя ссылка briefing Give Items на rune.t3.");
        }
        else
        {
            log.Skip("Контентные количества известных отсутствующих ссылок не проверяются для неизвестного SHA-256.");
        }

        DatabaseRecord powerHit = FindPowerHit(document);
        DatabaseField codeName = FindField(powerHit, "CodeName");
        string duplicateCode = document.Spells.Spells
            .Where(record => !ReferenceEquals(record, powerHit))
            .Select(record => record.GetString("CodeName"))
            .OfType<string>()
            .First(value => !string.IsNullOrWhiteSpace(value));
        SetForSemanticProbe(codeName, duplicateCode);
        AssertHasSemanticDiagnostic(
            document,
            "SEM_DUPLICATE_KEY",
            "spells.prototypes",
            powerHit,
            codeName,
            "Дубликат CodeName обнаружен в изменённой памяти.",
            log);
        codeName.ResetToOriginal();

        SetForSemanticProbe(codeName, string.Empty);
        AssertHasSemanticDiagnostic(
            document,
            "SEM_REQUIRED_VALUE_MISSING",
            "spells.prototypes",
            powerHit,
            codeName,
            "Пустой обязательный CodeName обнаружен в изменённой памяти.",
            log);
        codeName.ResetToOriginal();

        DatabaseRecord unit = document.Units.Prototypes.First(record =>
            record.Fields.Any(field => field.Id == 1 && field.IsEditable));
        DatabaseField baseRace = unit.Fields.Single(field => field.Id == 1);
        SetForSemanticProbe(baseRace, "__missing_race_verification__");
        AssertHasSemanticDiagnostic(
            document,
            "SEM_REFERENCE_NOT_FOUND",
            "units.prototypes",
            unit,
            baseRace,
            "Неизвестная Base Race обнаружена с точным полем.",
            log);
        baseRace.ResetToOriginal();

        DatabaseField area = powerHit.Fields.Single(field => field.Id == 9);
        SetForSemanticProbe(area, "-1");
        AssertHasSemanticDiagnostic(
            document,
            "SEM_VALUE_OUT_OF_RANGE",
            "spells.prototypes",
            powerHit,
            area,
            "Отрицательная область заклинания обнаружена проверкой диапазона.",
            log);
        area.ResetToOriginal();

        log.Assert(
            document.Validate().SequenceEqual(first) && !document.IsModified,
            "ResetToOriginal полностью восстановил исходную семантическую диагностику.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            document.Validate(cancelled.Token);
            throw new VerificationException("Отменённая семантическая проверка неожиданно завершилась успешно.");
        }
        catch (OperationCanceledException)
        {
            log.Pass("Семантическая проверка поддерживает отмену.");
        }
    }

    private static void SetForSemanticProbe(DatabaseField field, string value)
    {
        if (!field.TrySetValue(value, out string? error))
        {
            throw new VerificationException(
                $"Не удалось установить тестовое значение поля {field.Path}: {error}");
        }
    }

    private static void AssertHasSemanticDiagnostic(
        DatabaseDocument document,
        string code,
        string tableKey,
        DatabaseRecord record,
        DatabaseField field,
        string message,
        VerificationLog log)
    {
        ValidationDiagnostic? diagnostic = document.Validate().FirstOrDefault(candidate =>
            candidate.Domain == DiagnosticDomain.Semantic &&
            candidate.Code == code &&
            candidate.Location?.RecordIndex == record.RecordIndex &&
            candidate.Location.TableKey == tableKey &&
            candidate.Location.FieldId == field.Id);
        log.Assert(
            diagnostic is not null && !diagnostic.IsBlocking,
            message);
    }

    private static async Task VerifyChangedStagingSourceIsRejectedAsync(
        string sourcePath,
        IReadOnlyCollection<string> allSourcePaths,
        SafeStagingArea staging,
        VerificationLog log)
    {
        string stagedSource = CopySourceToStaging(sourcePath, allSourcePaths, staging);
        DatabaseDocument document = await DatabaseDocument.OpenAsync(stagedSource).ConfigureAwait(false);
        FileSnapshot openedSnapshot = await FileSnapshot.CaptureAsync(stagedSource).ConfigureAwait(false);

        long mutationOffset = openedSnapshot.Length - 1;
        if (mutationOffset < ResArchiveDocument.HeaderSize)
        {
            throw new VerificationException("Staging-копия слишком мала для безопасной hash-пробы.");
        }

        byte originalByte = ReadByte(stagedSource, mutationOffset);
        WriteByte(stagedSource, mutationOffset, (byte)(originalByte ^ 0x01));
        FileSnapshot mutatedSnapshot = await FileSnapshot.CaptureAsync(stagedSource).ConfigureAwait(false);
        log.Assert(
            !openedSnapshot.ContentEquals(mutatedSnapshot),
            "Staging-источник действительно изменён после DatabaseDocument.Open.");

        string targetPath = staging.GetNewOutputPath($"{Guid.NewGuid():N}-changed-source-output.res");
        staging.AssertOutputPath(targetPath, allSourcePaths);
        IOException exception = await ExpectThrowsAsync<IOException>(
            () => document.SaveAsAsync(targetPath),
            "SaveAs должен отклонить изменённый staging-источник.").ConfigureAwait(false);
        log.Assert(
            exception.Message.Contains("изменился", StringComparison.OrdinalIgnoreCase),
            "SaveAs сообщил об изменении источника после открытия.");
        AssertNoOutputArtifacts(targetPath, log, "изменённого staging-источника");
    }

    private static async Task VerifySyntheticGamePathIsRejectedAsync(
        string sourcePath,
        IReadOnlyCollection<string> allSourcePaths,
        SafeStagingArea staging,
        VerificationLog log)
    {
        DatabaseDocument document = await DatabaseDocument.OpenAsync(sourcePath).ConfigureAwait(false);
        string targetPath = Path.Combine(
            staging.RootPath,
            "Starter",
            "MODS",
            "SyntheticSafetyProbe",
            $"{Guid.NewGuid():N}.res");
        staging.AssertOutputPath(targetPath, allSourcePaths);

        log.Assert(
            DatabaseSafetyPolicy.IsLikelyGameInstallationPath(targetPath),
            "Синтетический Starter\\MODS путь распознан как игровой без обращения к игре.");
        InvalidOperationException exception = await ExpectThrowsAsync<InvalidOperationException>(
            () => document.SaveAsAsync(targetPath),
            "SaveAs должен отклонить синтетический игровой путь.").ConfigureAwait(false);
        log.Assert(
            exception.Message.Contains("каталог игры", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("Starter", StringComparison.OrdinalIgnoreCase),
            "SaveAs вернул понятную причину запрета игрового пути.");
        AssertNoOutputArtifacts(targetPath, log, "синтетического игрового пути");
    }

    private static void VerifyInvalidInputsDoNotModifyDocument(
        string sourcePath,
        VerificationLog log)
    {
        DatabaseDocument document = DatabaseDocument.Open(sourcePath);
        FieldTarget byteTarget = FindEditableByteField(document);
        FieldState byteBefore = CaptureState(
            document,
            byteTarget.Record,
            byteTarget.Field);

        bool byteAccepted = byteTarget.Field.TrySetValue("256", out string? byteError);
        log.Assert(
            !byteAccepted && !string.IsNullOrWhiteSpace(byteError),
            "Выходящее за диапазон Byte значение «256» отклонено с объяснением.");
        AssertStateEquals(
            byteBefore,
            CaptureState(document, byteTarget.Record, byteTarget.Field),
            "Отклонённый Byte не изменил состояние поля, записи и документа.",
            log);

        DatabaseRecord powerHit = FindPowerHit(document);
        DatabaseField nameField = FindField(powerHit, "Name");
        FieldState nameBefore = CaptureState(document, powerHit, nameField);
        string invalidName = FormatValue(nameField.Value) + "\0verification";
        bool nameAccepted = nameField.TrySetValue(invalidName, out string? nameError);
        log.Assert(
            !nameAccepted &&
            !string.IsNullOrWhiteSpace(nameError) &&
            nameError.Contains("NUL", StringComparison.OrdinalIgnoreCase),
            "Строка со встроенным NUL отклонена с объяснением.");
        AssertStateEquals(
            nameBefore,
            CaptureState(document, powerHit, nameField),
            "Отклонённая NUL-строка не изменила состояние поля, записи и документа.",
            log);
    }

    private static async Task VerifyByteStateAndResetAsync(
        string sourcePath,
        IReadOnlyCollection<string> allSourcePaths,
        SafeStagingArea staging,
        VerificationLog log)
    {
        DatabaseDocument document = await DatabaseDocument.OpenAsync(sourcePath).ConfigureAwait(false);
        FieldTarget byteTarget = FindEditableByteField(document);
        DatabaseRecord byteRecord = byteTarget.Record;
        DatabaseField byteField = byteTarget.Field;
        if (byteField.Length != 1 || byteField.OriginalValue is not byte originalRaw)
        {
            throw new VerificationException("Byte-поле не имеет ожидаемый однобайтовый payload.");
        }

        FieldState originalState = CaptureState(document, byteRecord, byteField);
        DatabaseFieldState originalPayloadState = byteField.CaptureState();
        log.Assert(
            !originalState.FieldModified && !originalState.RecordModified && !originalState.DocumentModified,
            "Исходное Byte-поле не считается пользовательским изменением.");

        byte editedRaw = (byte)(originalRaw ^ 0x01);
        bool accepted = byteField.TrySetValue(
            editedRaw.ToString(CultureInfo.InvariantCulture),
            out string? error);
        log.Assert(
            accepted && string.IsNullOrEmpty(error) && byteField.Value is byte edited && edited == editedRaw,
            "Корректное Byte-значение принято без bool-канонизации.");
        DatabaseFieldState editedPayloadState = byteField.CaptureState();
        log.Assert(
            byteField.IsModified &&
            byteRecord.IsModified &&
            document.IsModified,
            "Изменение Byte отражено на всех уровнях состояния.");
        log.Assert(
            !originalPayloadState.IsEquivalentTo(editedPayloadState),
            "Opaque state различает исходное и изменённое Byte-значения.");

        byte alternateRaw = (byte)(originalRaw ^ 0x02);
        bool alternateAccepted = byteField.TrySetValue(
            alternateRaw.ToString(CultureInfo.InvariantCulture),
            out string? alternateError);
        log.Assert(
            alternateAccepted &&
            string.IsNullOrEmpty(alternateError) &&
            byteField.Value is byte alternate &&
            alternate == alternateRaw &&
            byteField.IsModified,
            "Второе Byte-значение принято перед эмуляцией Undo.");
        log.Assert(
            !editedPayloadState.IsEquivalentTo(byteField.CaptureState()),
            "Opaque state различает два изменённых Byte-значения.");

        byteField.RestoreState(editedPayloadState);
        log.Assert(
            byteField.Value is byte restored &&
            restored == editedRaw &&
            byteField.IsModified &&
            byteRecord.IsModified &&
            document.IsModified,
            "RestoreState восстановил первое Byte-значение и modified-флаги.");
        log.Assert(
            editedPayloadState.IsEquivalentTo(byteField.CaptureState()),
            "Снимок первого Byte-значения эквивалентен состоянию после RestoreState.");

        byteField.ResetToOriginal();
        log.Assert(
            originalPayloadState.IsEquivalentTo(byteField.CaptureState()),
            "Исходный opaque state эквивалентен текущему состоянию после ResetToOriginal.");
        AssertStateEquals(
            originalState,
            CaptureState(document, byteRecord, byteField),
            "ResetToOriginal восстановил исходное Byte-состояние.",
            log);

        string resetOutput = staging.GetNewOutputPath($"{Guid.NewGuid():N}-byte-reset.res");
        staging.AssertOutputPath(resetOutput, allSourcePaths);
        await document.SaveAsAsync(resetOutput).ConfigureAwait(false);
        log.Assert(
            await FileByteComparer.AreEqualAsync(sourcePath, resetOutput).ConfigureAwait(false),
            "SaveAs после Byte ResetToOriginal побайтово идентичен источнику.");
        AssertNoPartialArtifacts(resetOutput, log, "Byte ResetToOriginal");
    }

    private static async Task VerifyDynamicNameAndResetAsync(
        string sourcePath,
        IReadOnlyCollection<string> allSourcePaths,
        SafeStagingArea staging,
        VerificationLog log)
    {
        DatabaseDocument document = await DatabaseDocument.OpenAsync(sourcePath).ConfigureAwait(false);
        DatabaseRecord powerHit = FindPowerHit(document);
        DatabaseField nameField = FindField(powerHit, "Name");
        DatabaseField codeNameField = FindField(powerHit, "CodeName");
        FieldState originalCodeState = CaptureState(document, powerHit, codeNameField);
        string originalName = FormatValue(nameField.Value);
        string originalCodeName = FormatValue(codeNameField.Value);
        log.Assert(
            string.Equals(powerHit.Name, originalName, StringComparison.Ordinal) &&
            string.Equals(powerHit.DisplayName, originalName, StringComparison.Ordinal) &&
            string.Equals(powerHit.GetString("CodeName"), originalCodeName, StringComparison.Ordinal),
            "Отображаемое имя заклинания берётся из поля Name, а поиск — из отдельного CodeName.");

        string editedCodeName = originalCodeName + "_verification";
        bool codeAccepted = codeNameField.TrySetValue(editedCodeName, out string? codeError);
        log.Assert(
            codeAccepted && string.IsNullOrEmpty(codeError),
            "Тестовое изменение powerhit.CodeName принято в памяти.");
        log.Assert(
            string.Equals(powerHit.GetString("CodeName"), editedCodeName, StringComparison.Ordinal) &&
            string.Equals(powerHit.Name, originalName, StringComparison.Ordinal) &&
            string.Equals(powerHit.DisplayName, originalName, StringComparison.Ordinal),
            "Изменение CodeName не подменяет отображаемое поле Name.");
        log.Assert(
            codeNameField.IsModified && powerHit.IsModified && document.IsModified,
            "Изменение CodeName отражено на всех уровнях состояния.");

        codeNameField.ResetToOriginal();
        AssertStateEquals(
            originalCodeState,
            CaptureState(document, powerHit, codeNameField),
            "ResetToOriginal восстановил CodeName и флаги состояния.",
            log);

        FieldState originalNameState = CaptureState(document, powerHit, nameField);
        string editedName = originalName + "я";
        bool nameAccepted = nameField.TrySetValue(editedName, out string? nameError);
        log.Assert(
            nameAccepted && string.IsNullOrEmpty(nameError),
            "Тестовое изменение powerhit.Name принято в памяти.");
        log.Assert(
            string.Equals(powerHit.Name, editedName, StringComparison.Ordinal) &&
            string.Equals(powerHit.DisplayName, editedName, StringComparison.Ordinal) &&
            string.Equals(powerHit.GetString("CodeName"), originalCodeName, StringComparison.Ordinal),
            "Name и DisplayName следуют за полем Name, не затрагивая CodeName.");

        nameField.ResetToOriginal();
        AssertStateEquals(
            originalNameState,
            CaptureState(document, powerHit, nameField),
            "ResetToOriginal восстановил Name, динамическое имя и флаги состояния.",
            log);

        string resetOutput = staging.GetNewOutputPath($"{Guid.NewGuid():N}-dynamic-name-reset.res");
        staging.AssertOutputPath(resetOutput, allSourcePaths);
        await document.SaveAsAsync(resetOutput).ConfigureAwait(false);
        log.Assert(
            await FileByteComparer.AreEqualAsync(sourcePath, resetOutput).ConfigureAwait(false),
            "SaveAs после CodeName/Name ResetToOriginal побайтово идентичен источнику.");
        AssertNoPartialArtifacts(resetOutput, log, "dynamic-name ResetToOriginal");
    }

    private static async Task VerifyJunctionPathIsRejectedOrSkippedAsync(
        string sourcePath,
        IReadOnlyCollection<string> allSourcePaths,
        SafeStagingArea staging,
        VerificationLog log)
    {
        string suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        string realDirectory = Path.Combine(staging.RootPath, $"junction-target-{suffix}");
        string linkDirectory = Path.Combine(staging.RootPath, $"junction-link-{suffix}");
        string realProbe = Path.Combine(realDirectory, "probe.res");
        string linkProbe = Path.Combine(linkDirectory, "probe.res");
        staging.AssertOutputPath(realProbe, allSourcePaths);
        staging.AssertOutputPath(linkProbe, allSourcePaths);
        Directory.CreateDirectory(realDirectory);

        try
        {
            Directory.CreateSymbolicLink(linkDirectory, realDirectory);
        }
        catch (Exception linkCreationException) when (linkCreationException is
            UnauthorizedAccessException or
            PlatformNotSupportedException or
            NotSupportedException or
            SecurityException or
            IOException)
        {
            log.Skip(
                $"Junction/symlink regression: создание ссылки недоступно ({linkCreationException.Message}).");
            return;
        }

        log.Assert(
            DatabaseSafetyPolicy.ContainsReparsePoint(linkProbe),
            "Путь через staging-symlink распознан как reparse point.");
        DatabaseDocument document = await DatabaseDocument.OpenAsync(sourcePath).ConfigureAwait(false);
        InvalidOperationException rejectionException = await ExpectThrowsAsync<InvalidOperationException>(
            () => document.SaveAsAsync(linkProbe),
            "SaveAs должен отклонить путь через staging-symlink.").ConfigureAwait(false);
        log.Assert(
            rejectionException.Message.Contains("junction", StringComparison.OrdinalIgnoreCase) ||
            rejectionException.Message.Contains("символическую", StringComparison.OrdinalIgnoreCase),
            "SaveAs вернул понятную причину запрета reparse-point пути.");
        AssertNoOutputArtifacts(linkProbe, log, "staging-symlink пути");
        log.Assert(
            !File.Exists(realProbe),
            "Запрет symlink-пути не создал файл в реальном каталоге назначения.");
    }

    private static string CopySourceToStaging(
        string sourcePath,
        IReadOnlyCollection<string> allSourcePaths,
        SafeStagingArea staging)
    {
        string stagedSource = staging.GetNewOutputPath($"{Guid.NewGuid():N}-staged-source.res");
        staging.AssertOutputPath(stagedSource, allSourcePaths);
        File.Copy(sourcePath, stagedSource, overwrite: false);
        return stagedSource;
    }

    private static DatabaseRecord FindPowerHit(DatabaseDocument document) =>
        document.Spells.Spells.FirstOrDefault(record =>
            string.Equals(record.GetString("CodeName"), "powerhit", StringComparison.OrdinalIgnoreCase))
        ?? throw new VerificationException("Запись powerhit не найдена.");

    private static FieldTarget FindEditableByteField(DatabaseDocument document)
    {
        foreach (DatabaseRecord record in EnumerateRecords(document))
        {
            DatabaseField? field = record.Fields.FirstOrDefault(candidate =>
                candidate.IsEditable &&
                candidate.Length == 1 &&
                string.Equals(candidate.Kind, "Byte", StringComparison.Ordinal));
            if (field is not null)
            {
                return new FieldTarget(record, field);
            }
        }

        throw new VerificationException("В базе не найдено редактируемое однобайтовое Byte-поле.");
    }

    private static IEnumerable<DatabaseRecord> EnumerateRecords(DatabaseDocument document) =>
        document.Units.HitLocationTypes
            .Concat(document.Units.Races)
            .Concat(document.Units.Prototypes)
            .Concat(document.Units.Npcs)
            .Concat(document.Spells.Spells)
            .Concat(document.Spells.Runes)
            .Concat(document.Spells.ComplexSpells)
            .Concat(document.Spells.ComplexArmors)
            .Concat(document.Spells.ComplexWeapons);

    private static DatabaseField FindField(DatabaseRecord record, string key) =>
        record.Fields.FirstOrDefault(field =>
            string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? throw new VerificationException($"Поле {record.Path}/{key} не найдено.");

    private static FieldState CaptureState(
        DatabaseDocument document,
        DatabaseRecord record,
        DatabaseField field) => new(
            FormatValue(field.Value),
            FormatValue(field.OriginalValue),
            field.IsModified,
            record.IsModified,
            document.IsModified,
            record.Name,
            record.DisplayName);

    private static void AssertStateEquals(
        FieldState expected,
        FieldState actual,
        string message,
        VerificationLog log) => log.Assert(expected == actual, message);

    private static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty,
    };

    private static byte ReadByte(string path, long offset)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.RandomAccess);
        if (offset < 0 || offset >= stream.Length)
        {
            throw new VerificationException($"Смещение {offset} выходит за staging-файл {path}.");
        }

        stream.Position = offset;
        int value = stream.ReadByte();
        return value >= 0
            ? (byte)value
            : throw new EndOfStreamException($"Не удалось прочитать staging-файл {path}.");
    }

    private static void WriteByte(string path, long offset, byte value)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.RandomAccess | FileOptions.WriteThrough);
        if (offset < 0 || offset >= stream.Length)
        {
            throw new VerificationException($"Смещение {offset} выходит за staging-файл {path}.");
        }

        stream.Position = offset;
        stream.WriteByte(value);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<TException> ExpectThrowsAsync<TException>(
        Func<Task> action,
        string failureMessage)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            throw new VerificationException(
                $"{failureMessage} Получено {exception.GetType().Name}: {exception.Message}");
        }

        throw new VerificationException($"{failureMessage} Исключение не возникло.");
    }

    private static void AssertNoOutputArtifacts(
        string targetPath,
        VerificationLog log,
        string scenario)
    {
        log.Assert(!File.Exists(targetPath), $"Отказ для {scenario} не создал целевой файл.");
        AssertNoPartialArtifacts(targetPath, log, scenario);
    }

    private static void AssertNoPartialArtifacts(
        string targetPath,
        VerificationLog log,
        string scenario)
    {
        string? directory = Path.GetDirectoryName(targetPath);
        string partialPattern = $".{Path.GetFileName(targetPath)}.partial-*";
        bool partialExists = !string.IsNullOrWhiteSpace(directory) &&
            Directory.Exists(directory) &&
            Directory.EnumerateFiles(directory, partialPattern, SearchOption.TopDirectoryOnly).Any();
        log.Assert(!partialExists, $"Сценарий {scenario} не оставил временный .partial-файл.");
    }

    private sealed record FieldState(
        string Value,
        string OriginalValue,
        bool FieldModified,
        bool RecordModified,
        bool DocumentModified,
        string RecordName,
        string RecordDisplayName);

    private sealed record FieldTarget(DatabaseRecord Record, DatabaseField Field);
}
