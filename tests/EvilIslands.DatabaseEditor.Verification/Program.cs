using System.Globalization;
using System.Text;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.Verification;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var log = new VerificationLog();

        VerificationOptions options;
        try
        {
            options = VerificationOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            log.Fail(exception.Message);
            PrintUsage();
            log.PrintSummary();
            return 2;
        }

        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        if (options.RepairSourcePath is not null && options.RepairReferencePath is not null)
        {
            return await RepairEditorSplitStringListsAsync(
                options.RepairSourcePath,
                options.RepairReferencePath,
                log).ConfigureAwait(false);
        }

        string[] existingSources = options.SourcePaths.Where(File.Exists).ToArray();
        foreach (string missingSource in options.SourcePaths.Except(existingSources, StringComparer.OrdinalIgnoreCase))
        {
            log.Skip($"Источник отсутствует: {missingSource}");
        }

        if (existingSources.Length == 0)
        {
            string message = "Не найдено ни одного database.res; файловые проверки не запускались.";
            if (options.RequireSources)
            {
                log.Fail(message);
            }
            else
            {
                log.Skip(message);
            }

            log.PrintSummary();
            return log.Failures.Count == 0 ? 0 : 1;
        }

        SafeStagingArea staging = SafeStagingArea.Create();
        log.Info($"Тестовые результаты создаются только здесь: {staging.RootPath}");

        foreach (string sourcePath in existingSources)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {sourcePath} ===");
            try
            {
                await VerifySourceAsync(sourcePath, existingSources, staging, log).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                log.Fail($"{sourcePath}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        log.PrintSummary();
        return log.Failures.Count == 0 ? 0 : 1;
    }

    private static async Task<int> RepairEditorSplitStringListsAsync(
        string sourcePath,
        string referencePath,
        VerificationLog log)
    {
        if (!File.Exists(sourcePath) || !File.Exists(referencePath))
        {
            log.Fail(
                $"Для восстановления нужны оба существующих файла: source={sourcePath}; reference={referencePath}.");
            log.PrintSummary();
            return 1;
        }

        SafeStagingArea staging = SafeStagingArea.Create();
        log.Info($"Восстановленная копия создаётся только здесь: {staging.RootPath}");
        FileSnapshot sourceBefore = await FileSnapshot.CaptureAsync(sourcePath).ConfigureAwait(false);
        FileSnapshot referenceBefore = await FileSnapshot.CaptureAsync(referencePath).ConfigureAwait(false);
        DatabaseDocument source = await DatabaseDocument.OpenAsync(sourcePath).ConfigureAwait(false);
        DatabaseDocument reference = await DatabaseDocument.OpenAsync(referencePath).ConfigureAwait(false);

        IReadOnlyList<EditorSplitStringListRepairResult> candidates =
            EditorSplitStringListRepair.FindCandidates(source, reference);
        if (candidates.Count == 0)
        {
            log.Fail("Не найдено ни одного доказуемо разделённого StringList-поля; файл не создавался.");
            log.PrintSummary();
            return 1;
        }

        foreach (EditorSplitStringListRepairResult candidate in candidates)
        {
            log.Info(
                $"Кандидат: {candidate.TableKey}[{candidate.RecordIndex}] " +
                $"«{candidate.RecordName}»/field-{candidate.FieldId} «{candidate.FieldName}»: " +
                $"{candidate.OriginalElementCount} -> {candidate.RepairedElementCount} элементов, " +
                $"склеек: {candidate.JoinedFragmentGroups}.");
        }

        IReadOnlyList<EditorSplitStringListRepairResult> repaired =
            EditorSplitStringListRepair.Repair(source, reference);
        log.Assert(
            repaired.SequenceEqual(candidates),
            "Набор применённых исправлений совпадает с предварительно найденными кандидатами.");

        string outputPath = staging.GetNewOutputPath(
            $"{Path.GetFileNameWithoutExtension(sourcePath)}.repaired-{Guid.NewGuid():N}.res");
        staging.AssertOutputPath(outputPath, [sourcePath, referencePath]);
        SaveResult saved = await source.SaveAsAsync(outputPath).ConfigureAwait(false);
        log.Assert(
            saved.ReopenedSuccessfully && File.Exists(outputPath),
            "Восстановленная копия записана в staging и повторно открыта Core.");

        DatabaseDocument reopened = await DatabaseDocument.OpenAsync(outputPath).ConfigureAwait(false);
        AssertResourceIdentity(
            ResArchiveDocument.Open(sourcePath),
            ResArchiveDocument.Open(outputPath),
            changedResourceName: "units.udb",
            log);
        ValidationDiagnostic[] blocking = reopened.Validate()
            .Where(diagnostic => diagnostic.IsBlocking)
            .ToArray();
        log.Assert(blocking.Length == 0, "Восстановленная копия не содержит блокирующих ошибок.");
        log.Assert(
            EditorSplitStringListRepair.FindCandidates(reopened, reference).Count == 0,
            "После повторного открытия доказуемо разделённых StringList-полей не осталось.");

        FileSnapshot sourceAfter = await FileSnapshot.CaptureAsync(sourcePath).ConfigureAwait(false);
        FileSnapshot referenceAfter = await FileSnapshot.CaptureAsync(referencePath).ConfigureAwait(false);
        log.Assert(sourceBefore.ContentEquals(sourceAfter), "Повреждённый источник не изменён режимом восстановления.");
        log.Assert(referenceBefore.ContentEquals(referenceAfter), "Опорная база не изменена режимом восстановления.");
        log.Info($"REPAIRED_OUTPUT={outputPath}");
        log.Info($"REPAIRED_SHA256={saved.Sha256}");
        log.PrintSummary();
        return log.Failures.Count == 0 ? 0 : 1;
    }

    private static async Task VerifySourceAsync(
        string sourcePath,
        IReadOnlyCollection<string> allSourcePaths,
        SafeStagingArea staging,
        VerificationLog log)
    {
        FileSnapshot sourceBefore = await FileSnapshot.CaptureAsync(sourcePath).ConfigureAwait(false);
        log.Info($"Источник: {sourceBefore.Length:N0} bytes; SHA-256 {sourceBefore.Sha256}");
        try
        {
            ResArchiveDocument archive = await log.MeasureAsync(
                "Открытие RES",
                () => ResArchiveDocument.OpenAsync(sourcePath)).ConfigureAwait(false);

            log.Assert(archive.Length == sourceBefore.Length, "Размер разобранного RES совпадает с файлом.");
            log.Assert(archive.Entries.Count > 0, "Каталог RES содержит ресурсы.");
            log.Assert(
                archive.DeclaredEntryCount >= archive.Entries.Count,
                "Количество разобранных ресурсов не превышает объявленное.");

            ValidationDiagnostic[] archiveErrors = archive.Validate()
                .Where(item => item.Severity == DiagnosticSeverity.Error)
                .ToArray();
            log.Assert(archiveErrors.Length == 0, "Заголовок, каталог и диапазоны ресурсов RES корректны.");

            IReadOnlyList<DatabaseBoundarySummary> boundaries = await log.MeasureAsync(
                "Проверка границ внутренних DB",
                () => Task.FromResult(DatabaseBoundaryVerifier.Verify(archive))).ConfigureAwait(false);
            foreach (DatabaseBoundarySummary summary in boundaries)
            {
                log.Info(
                    $"{summary.ResourceName}: top={summary.TopLevelChunks}, " +
                    $"sections={summary.Sections}, records={summary.Records}, fields={summary.Fields}");
            }

            log.Assert(boundaries.All(item => item.Fields > 0), "Все внутренние DB разобраны до границ полей.");

            CoreDocumentAdapter coreDocument = await log.MeasureAsync(
                "Открытие DatabaseDocument",
                () => CoreDocumentAdapter.OpenAsync(sourcePath)).ConfigureAwait(false);
            AssertCoreValidation(coreDocument, log, "Исходный DatabaseDocument", sourceBefore.Sha256);
            AssertDatabaseCatalog(coreDocument, boundaries, sourceBefore.Sha256, log);
            AssertEmbeddedMagicCatalog(coreDocument.GetDatabases(), log);
            log.Assert(coreDocument.ContainsPowerHit(), "В spells.sdb найдена запись CodeName=powerhit.");

            await VerifyDatabaseComparisonAsync(sourcePath, log).ConfigureAwait(false);

            string noOpPath = staging.GetNewOutputPath($"{Guid.NewGuid():N}-noop.res");
            staging.AssertOutputPath(noOpPath, allSourcePaths);
            await log.MeasureAsync(
                "No-op Save As",
                async () =>
                {
                    await coreDocument.SaveAsAsync(noOpPath).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
            log.Assert(File.Exists(noOpPath), "No-op копия создана только в staging-каталоге.");
            log.Assert(
                await FileByteComparer.AreEqualAsync(sourcePath, noOpPath).ConfigureAwait(false),
                "No-op Save As побайтово идентичен источнику.");

            CoreDocumentAdapter reopenedNoOp = await log.MeasureAsync(
                "Повторное открытие no-op копии",
                () => CoreDocumentAdapter.OpenAsync(noOpPath)).ConfigureAwait(false);
            AssertCoreValidation(reopenedNoOp, log, "No-op копия");
            log.Assert(reopenedNoOp.ContainsPowerHit(), "powerhit сохранился в no-op копии.");

            ResArchiveDocument noOpArchive = ResArchiveDocument.Open(noOpPath);
            AssertResourceIdentity(archive, noOpArchive, changedResourceName: null, log);
            DatabaseBoundaryVerifier.Verify(noOpArchive);
            log.Pass("No-op копия повторно разобрана до границ всех DB-полей.");

            await VerifyCompanionTextsAsync(sourcePath, staging, log).ConfigureAwait(false);
            await VerifyCompanionSoundsAsync(sourcePath, staging, log).ConfigureAwait(false);

            await VerifyRowAppendSafetyAsync(
                sourcePath,
                allSourcePaths,
                staging,
                archive,
                boundaries.Sum(summary => summary.Records),
                log).ConfigureAwait(false);
            await VerifyComplexFieldRoundTripAsync(
                sourcePath,
                allSourcePaths,
                staging,
                log).ConfigureAwait(false);
            await VerifyStructuredAckListRoundTripAsync(
                sourcePath,
                allSourcePaths,
                staging,
                log).ConfigureAwait(false);
            await VerifyNestedStringListDelimiterSafetyAsync(sourcePath, log).ConfigureAwait(false);
            await VerifyCommaSeparatedStringListSafetyAsync(sourcePath, log).ConfigureAwait(false);

            CoreDocumentAdapter editedDocument = await log.MeasureAsync(
                "Повторное открытие для тестового изменения",
                () => CoreDocumentAdapter.OpenAsync(sourcePath)).ConfigureAwait(false);
            EditProbe edit = editedDocument.ApplyPowerHitScalarEdit();
            log.Pass(
                $"Тестовое изменение сделано только в памяти: " +
                $"{edit.RecordCodeName}.{edit.FieldKey} {edit.OriginalValue} -> {edit.EditedValue}.");

            string editedPath = staging.GetNewOutputPath($"{Guid.NewGuid():N}-edited.res");
            staging.AssertOutputPath(editedPath, allSourcePaths);
            await log.MeasureAsync(
                "Изменённый Save As",
                async () =>
                {
                    await editedDocument.SaveAsAsync(editedPath).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
            log.Assert(File.Exists(editedPath), "Изменённая копия создана только в staging-каталоге.");

            CoreDocumentAdapter reopenedEdited = await log.MeasureAsync(
                "Повторное открытие изменённой копии",
                () => CoreDocumentAdapter.OpenAsync(editedPath)).ConfigureAwait(false);
            AssertCoreValidation(reopenedEdited, log, "Изменённая копия");
            log.Assert(reopenedEdited.ContainsPowerHit(), "powerhit сохранился в изменённой копии.");
            log.Assert(
                string.Equals(
                    reopenedEdited.GetPowerHitFieldValue(edit.FieldKey),
                    edit.EditedValue,
                    StringComparison.Ordinal),
                $"Значение powerhit.{edit.FieldKey} сохранилось и повторно прочитано.");

            ResArchiveDocument editedArchive = ResArchiveDocument.Open(editedPath);
            AssertResourceIdentity(archive, editedArchive, edit.ResourceName, log);
            DatabaseBoundaryVerifier.Verify(editedArchive);
            log.Pass("Изменённая копия повторно разобрана до границ всех DB-полей.");

            CoreDocumentAdapter resizedDocument = await log.MeasureAsync(
                "Открытие для CP1251-строкового изменения",
                () => CoreDocumentAdapter.OpenAsync(sourcePath)).ConfigureAwait(false);
            EditProbe stringEdit = resizedDocument.ApplyPowerHitVariableLengthStringEdit();
            log.Pass(
                $"CP1251-строка увеличена на один байт только в памяти: " +
                $"{stringEdit.RecordCodeName}.{stringEdit.FieldKey}.");

            string resizedPath = staging.GetNewOutputPath($"{Guid.NewGuid():N}-resized.res");
            staging.AssertOutputPath(resizedPath, allSourcePaths);
            await log.MeasureAsync(
                "Variable-length CP1251 Save As",
                async () =>
                {
                    await resizedDocument.SaveAsAsync(resizedPath).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
            log.Assert(File.Exists(resizedPath), "Строковая копия создана только в staging-каталоге.");

            CoreDocumentAdapter reopenedResized = await log.MeasureAsync(
                "Повторное открытие строковой копии",
                () => CoreDocumentAdapter.OpenAsync(resizedPath)).ConfigureAwait(false);
            AssertCoreValidation(reopenedResized, log, "Строковая копия");
            log.Assert(
                string.Equals(
                    reopenedResized.GetPowerHitFieldValue(stringEdit.FieldKey),
                    stringEdit.EditedValue,
                    StringComparison.Ordinal),
                "Увеличенное CP1251-значение powerhit.Name сохранилось и повторно прочитано.");

            ResArchiveDocument resizedArchive = ResArchiveDocument.Open(resizedPath);
            AssertResourceIdentity(archive, resizedArchive, stringEdit.ResourceName, log);
            AssertResourceResize(archive, resizedArchive, stringEdit.ResourceName, log);
            DatabaseBoundaryVerifier.Verify(resizedArchive);
            log.Pass("Строковая копия повторно разобрана до границ всех DB-полей.");

            await SafetyRegressionVerifier.RunAsync(
                sourcePath,
                allSourcePaths,
                staging,
                sourceBefore.Sha256,
                log).ConfigureAwait(false);

            FileSnapshot noOpSnapshot = await FileSnapshot.CaptureAsync(noOpPath).ConfigureAwait(false);
            FileSnapshot editedSnapshot = await FileSnapshot.CaptureAsync(editedPath).ConfigureAwait(false);
            FileSnapshot resizedSnapshot = await FileSnapshot.CaptureAsync(resizedPath).ConfigureAwait(false);
            log.Info($"No-op:   {noOpSnapshot.Length:N0} bytes; SHA-256 {noOpSnapshot.Sha256}");
            log.Info($"Edited:  {editedSnapshot.Length:N0} bytes; SHA-256 {editedSnapshot.Sha256}");
            log.Info($"Resized: {resizedSnapshot.Length:N0} bytes; SHA-256 {resizedSnapshot.Sha256}");
        }
        finally
        {
            FileSnapshot sourceAfter = await FileSnapshot.CaptureAsync(sourcePath).ConfigureAwait(false);
            log.Assert(
                sourceBefore.ContentEquals(sourceAfter),
                "SHA-256 источника не изменился после всех операций.");
        }
    }

    private static async Task VerifyCompanionSoundsAsync(
        string sourcePath,
        SafeStagingArea staging,
        VerificationLog log)
    {
        string directory = Path.GetDirectoryName(sourcePath)!;
        string[] soundPaths =
        [
            Path.Combine(directory, "JmvSFX.res"),
            Path.Combine(directory, "sfx.res"),
            Path.Combine(directory, "speech.res"),
            Path.Combine(directory, "atamannala.res"),
        ];
        soundPaths = soundPaths.Where(File.Exists).ToArray();
        if (soundPaths.Length == 0)
        {
            log.Skip("Рядом с database.res нет известных звуковых RES; проверка связей звука пропущена.");
            return;
        }

        FileSnapshot[] before = await Task.WhenAll(soundPaths.Select(path => FileSnapshot.CaptureAsync(path)))
            .ConfigureAwait(false);
        SoundArchiveCatalog catalog = await log.MeasureAsync(
            "Каталог звуковых RES",
            () => SoundArchiveCatalog.OpenAsync(soundPaths)).ConfigureAwait(false);
        DatabaseDocument document = await DatabaseDocument.OpenAsync(sourcePath).ConfigureAwait(false);
        var links = document.Databases
            .SelectMany(group => group.Tables)
            .SelectMany(table => table.Records.SelectMany(record =>
                DatabaseSoundLinkResolver.Resolve(table.Key, record, catalog)))
            .ToArray();
        DatabaseSoundLink[] direct = links
            .Where(link => link.Kind == DatabaseSoundLinkKind.DirectResource)
            .ToArray();
        DatabaseSoundLink[] briefings = links
            .Where(link => link.Kind == DatabaseSoundLinkKind.BriefingFolder)
            .ToArray();
        int directResolved = direct.Count(link => link.IsResolved);
        int briefingResolved = briefings.Count(link => link.IsResolved);

        log.Assert(catalog.Archives.Count == soundPaths.Length, "Все выбранные звуковые RES открыты один раз.");
        log.Assert(catalog.Resources.Count > 0, "В подключённых RES распознаны WAV/MP3/OGG.");
        if (catalog.Resources.Any(resource => resource.ResourceName.StartsWith("Acks\\", StringComparison.OrdinalIgnoreCase)))
        {
            log.Assert(direct.Length > 0, "В acks.db извлечены прямые ссылки File из вложенных реплик.");
            log.Assert(directResolved > 0, "Прямые ссылки acks.db сопоставлены с файлами звуковых RES.");
        }

        if (catalog.Resources.Any(resource => resource.ResourceName.Contains("\\Briefing\\", StringComparison.OrdinalIgnoreCase)))
        {
            log.Assert(briefings.Length > 0, "Для записей Брифингов построены ожидаемые звуковые каталоги.");
            log.Assert(briefingResolved > 0, "Ключи Брифингов сопоставлены с MP3 в speech.res.");
        }

        BriefingPhrase[] parserProbe = BriefingTranscriptParser.Parse(
            "#phrase Hero  1\r\nПервая \r\nреплика\r\n#CAMERA 3\r\n" +
            "#phrase Elder  2\r\nОтвет старосты").ToArray();
        log.Assert(
            parserProbe.Length == 2 &&
            parserProbe[0] == new BriefingPhrase(1, "Hero", "Первая реплика") &&
            parserProbe[1] == new BriefingPhrase(2, "Elder", "Ответ старосты"),
            "Разметка #phrase извлекает номер, персонажа и полный текст реплики.");
        log.Assert(
            BriefingTranscriptParser.TryGetClipIdentity(
                @"s\Briefing\z1\2a.mp3",
                out int probeNumber,
                out string probeClipId) &&
            probeNumber == 2 && probeClipId == "2a",
            "Имя MP3, включая буквенный вариант, сопоставляется с номером #phrase.");
        string editedProbe = BriefingTranscriptParser.UpdatePhrase(
            "#DESC +\r\n#phrase Hero 1\r\nСтарый текст\r\n#CAMERA 3\r\n",
            1,
            "Elder",
            "Новая строка\r\nи продолжение");
        BriefingPhrase[] editedPhrases = BriefingTranscriptParser.Parse(editedProbe).ToArray();
        log.Assert(
            editedPhrases.Length == 1 &&
            editedPhrases[0] == new BriefingPhrase(1, "Elder", "Новая строка и продолжение") &&
            editedProbe.Contains("#CAMERA 3", StringComparison.Ordinal),
            "Редактор реплики меняет только выбранный #phrase и сохраняет соседние команды брифинга.");
        string appendedProbe = BriefingTranscriptParser.UpdatePhrase(editedProbe, 2, "Hero", "Ответ героя");
        log.Assert(
            BriefingTranscriptParser.GetNextPhraseNumber(editedProbe) == 2 &&
            BriefingTranscriptParser.Parse(appendedProbe).Any(phrase =>
                phrase == new BriefingPhrase(2, "Hero", "Ответ героя")),
            "Новая реплика получает следующий номер и дописывается в BRIEFING-описание.");

        const string timelineProbe =
            "#DESC +\r\n#show Hero 0 weapon\r\n#ANIMATION 30\r\n#NOLIPS\r\n" +
            "#phrase Hero 1\r\nМысль героя\r\n#CAMERA 8\r\n#phrase Sheriff 2\r\nОтвет шерифа";
        BriefingTimelineEvent[] timelineEvents = BriefingTranscriptParser.ParseTimeline(timelineProbe).ToArray();
        log.Assert(
            timelineEvents.Select(briefingEvent => briefingEvent.Kind).SequenceEqual(
            [
                BriefingEventKind.Description,
                BriefingEventKind.Show,
                BriefingEventKind.Animation,
                BriefingEventKind.Thought,
                BriefingEventKind.Phrase,
                BriefingEventKind.Camera,
                BriefingEventKind.Phrase,
            ]) &&
            timelineEvents[4].PhraseNumber == 1 &&
            timelineEvents[4].Speaker == "Hero" &&
            timelineEvents[4].Text == "Мысль героя" &&
            timelineEvents[5].Arguments == "8",
            "Сценарная лента сохраняет порядок #DESC, появления, анимации, мысли, камеры и реплик.");
        string updatedTimelineProbe = BriefingTranscriptParser.UpdateTimelineEvent(
            timelineProbe,
            timelineEvents[5].Ordinal,
            "11 45 6 2",
            0,
            string.Empty,
            string.Empty);
        BriefingTimelineEvent[] updatedTimeline = BriefingTranscriptParser.ParseTimeline(updatedTimelineProbe).ToArray();
        log.Assert(
            updatedTimeline.Length == timelineEvents.Length &&
            updatedTimeline[5].Kind == BriefingEventKind.Camera &&
            updatedTimeline[5].Arguments == "11 45 6 2" &&
            updatedTimeline[4].Text == "Мысль героя" &&
            updatedTimeline[6].Text == "Ответ шерифа" &&
            updatedTimelineProbe.Contains("#NOLIPS", StringComparison.Ordinal),
            "Редактор события меняет только выбранную камеру и не повреждает соседние команды и реплики.");

        string insertedTimelineProbe = BriefingTranscriptParser.InsertTimelineEvent(
            timelineProbe,
            timelineEvents[3].Ordinal,
            BriefingEventKind.Camera,
            "12 35 7 2");
        BriefingTimelineEvent[] insertedTimeline = BriefingTranscriptParser.ParseTimeline(insertedTimelineProbe).ToArray();
        log.Assert(
            insertedTimeline.Length == timelineEvents.Length + 1 &&
            insertedTimeline[4].Kind == BriefingEventKind.Camera &&
            insertedTimeline[4].Arguments == "12 35 7 2" &&
            insertedTimeline[5].IsPhrase &&
            insertedTimeline[5].Text == "Мысль героя",
            "Новое событие вставляется сразу после выбранного шага, не повреждая следующую реплику.");

        string movedTimelineProbe = BriefingTranscriptParser.MoveTimelineEvent(
            insertedTimelineProbe,
            insertedTimeline[4].Ordinal,
            1);
        BriefingTimelineEvent[] movedTimeline = BriefingTranscriptParser.ParseTimeline(movedTimelineProbe).ToArray();
        log.Assert(
            movedTimeline[4].IsPhrase &&
            movedTimeline[4].Text == "Мысль героя" &&
            movedTimeline[5].Kind == BriefingEventKind.Camera &&
            movedTimeline[5].Arguments == "12 35 7 2",
            "Перемещение меняет местами целые соседние блоки и сохраняет текст реплики.");

        string removedTimelineProbe = BriefingTranscriptParser.RemoveTimelineEvent(
            movedTimelineProbe,
            movedTimeline[5].Ordinal);
        BriefingTimelineEvent[] removedTimeline = BriefingTranscriptParser.ParseTimeline(removedTimelineProbe).ToArray();
        log.Assert(
            removedTimeline.Select(briefingEvent => briefingEvent.Kind).SequenceEqual(
                timelineEvents.Select(briefingEvent => briefingEvent.Kind)) &&
            removedTimeline[4].Text == "Мысль героя" &&
            removedTimeline[6].Text == "Ответ шерифа",
            "Удаление убирает только выбранное событие и возвращает исходную сценарную последовательность.");

        string insertedPhraseProbe = BriefingTranscriptParser.InsertTimelineEvent(
            timelineProbe,
            timelineEvents[5].Ordinal,
            BriefingEventKind.Phrase,
            phraseNumber: 3,
            speaker: "Hero",
            text: "Новая реплика");
        BriefingTimelineEvent[] insertedPhraseTimeline = BriefingTranscriptParser.ParseTimeline(insertedPhraseProbe).ToArray();
        log.Assert(
            insertedPhraseTimeline[6].IsPhrase &&
            insertedPhraseTimeline[6].PhraseNumber == 3 &&
            insertedPhraseTimeline[6].Text == "Новая реплика" &&
            insertedPhraseTimeline[7].PhraseNumber == 2,
            "Новая реплика вставляется в выбранное место сценария, а не только в конец BRIEFING-ресурса.");

        string duplicatedCameraProbe = BriefingTranscriptParser.DuplicateTimelineEvent(
            timelineProbe,
            timelineEvents[5].Ordinal);
        BriefingTimelineEvent[] duplicatedCameraTimeline = BriefingTranscriptParser
            .ParseTimeline(duplicatedCameraProbe)
            .ToArray();
        log.Assert(
            duplicatedCameraTimeline.Length == timelineEvents.Length + 1 &&
            duplicatedCameraTimeline[5].Kind == BriefingEventKind.Camera &&
            duplicatedCameraTimeline[6].Kind == BriefingEventKind.Camera &&
            duplicatedCameraTimeline[5].Arguments == duplicatedCameraTimeline[6].Arguments,
            "Дублирование служебного события создаёт его точную копию сразу после исходного.");

        string duplicatedPhraseProbe = BriefingTranscriptParser.DuplicateTimelineEvent(
            timelineProbe,
            timelineEvents[6].Ordinal);
        BriefingTimelineEvent[] duplicatedPhraseTimeline = BriefingTranscriptParser
            .ParseTimeline(duplicatedPhraseProbe)
            .ToArray();
        log.Assert(
            duplicatedPhraseTimeline.Length == timelineEvents.Length + 1 &&
            duplicatedPhraseTimeline[^1].IsPhrase &&
            duplicatedPhraseTimeline[^1].PhraseNumber == 3 &&
            duplicatedPhraseTimeline[^1].Speaker == "Sheriff" &&
            duplicatedPhraseTimeline[^1].Text == "Ответ шерифа",
            "Дублирование реплики сохраняет персонажа и текст, но выдаёт следующий свободный номер #phrase.");

        const string invalidTimelineProbe =
            "#show Hero 0\r\n#show Hero 0\r\n#hide Elder\r\n#CAMERA bad\r\n" +
            "#ANIMATION nope\r\n#NOLIPS\r\n#phrase Hero 1\r\nПервая\r\n" +
            "#phrase Elder 1\r\nПовтор\r\n#phrase Hero 3\r\nТретья\r\n" +
            "#NOLIPS\r\n#phrase broken";
        BriefingTimelineIssue[] timelineIssues = BriefingTranscriptParser
            .ValidateTimeline(invalidTimelineProbe)
            .ToArray();
        log.Assert(
            timelineIssues.Count(issue => issue.Code == BriefingIssueCode.DuplicatePhraseNumber) == 2 &&
            timelineIssues.Any(issue => issue.Code == BriefingIssueCode.MissingPhraseNumber) &&
            timelineIssues.Any(issue => issue.Code == BriefingIssueCode.InvalidCamera) &&
            timelineIssues.Any(issue => issue.Code == BriefingIssueCode.InvalidAnimation) &&
            timelineIssues.Any(issue => issue.Code == BriefingIssueCode.OrphanThought) &&
            timelineIssues.Any(issue => issue.Code == BriefingIssueCode.DuplicateShow) &&
            timelineIssues.Any(issue => issue.Code == BriefingIssueCode.HideBeforeShow) &&
            timelineIssues.Any(issue => issue.Code == BriefingIssueCode.MalformedPhrase),
            "Проверка сценария находит дубликаты и пропуски #phrase, неверные камеру/анимацию, осиротевший #NOLIPS и порядок show/hide.");

        SoundResourceInfo extractionProbe = catalog.Resources
            .Where(resource => resource.Length > 0)
            .OrderBy(resource => resource.Length)
            .First();
        byte[] extractedBytes = await SoundArchiveCatalog.ReadResourceAsync(extractionProbe).ConfigureAwait(false);
        log.Assert(
            extractedBytes.Length == extractionProbe.Length,
            "Прослушивание извлекает из звукового RES точный диапазон выбранного WAV/MP3/OGG.");

        SoundResourceInfo briefingAudio = briefings.First(link => link.IsResolved && link.Resource is not null).Resource!;
        BriefingSoundImportTarget replacementTarget = SoundArchiveCopyEditor.ResolveBriefingTarget(
            catalog,
            "verification-scene",
            phraseNumber: 1,
            briefingAudio.Format,
            briefingAudio);
        log.Assert(
            replacementTarget.IsReplacement &&
            ReferenceEquals(replacementTarget.Archive, briefingAudio.Archive) &&
            replacementTarget.ResourceName.Equals(briefingAudio.ResourceName, StringComparison.OrdinalIgnoreCase),
            "Замена звука сохраняет точные архив, путь и формат существующей реплики.");
        string briefingDirectory = briefingAudio.ResourceName[..briefingAudio.ResourceName.LastIndexOf('\\')];
        string briefingKey = briefingDirectory[(briefingDirectory.LastIndexOf('\\') + 1)..];
        BriefingSoundImportTarget resolvedAddition = SoundArchiveCopyEditor.ResolveBriefingTarget(
            catalog,
            briefingKey,
            phraseNumber: 65_000,
            briefingAudio.Format);
        string resolvedDirectory = resolvedAddition.ResourceName[..resolvedAddition.ResourceName.LastIndexOf('\\')];
        log.Info(
            $"Автоимя звука: key={briefingKey}; archive={resolvedAddition.Archive.FileName}; " +
            $"resource={resolvedAddition.ResourceName}.");
        log.Assert(
            !resolvedAddition.IsReplacement &&
            resolvedAddition.ResourceName.EndsWith(
                $"\\65000.{briefingAudio.Format.ToLowerInvariant()}",
                StringComparison.OrdinalIgnoreCase) &&
            catalog.FindBriefing(briefingKey).Any(resource =>
                ReferenceEquals(resource.Archive, resolvedAddition.Archive) &&
                resource.ResourceName.StartsWith($"{resolvedDirectory}\\", StringComparison.OrdinalIgnoreCase)),
            "Новый звук получает архив и каталог текущей сцены, а имя — номер #phrase.");

        SoundArchiveInfo smallestArchive = catalog.Archives.OrderBy(archive => archive.FileLength).First();
        string probeName = $@"s\Briefing\verification-{Guid.NewGuid():N}\1.wav";
        string probeInputPath = staging.GetNewOutputPath($"{Guid.NewGuid():N}-audio-probe.wav");
        await File.WriteAllBytesAsync(probeInputPath, CreateProbeWav()).ConfigureAwait(false);
        SoundImportAsset probeAsset = await SoundArchiveCopyEditor.LoadImportAsync(probeInputPath)
            .ConfigureAwait(false);
        ResFileManagerArchive fileManagerArchive = await ResFileManagerArchive.OpenAsync(smallestArchive.SourcePath)
            .ConfigureAwait(false);
        ResEntry[] fileManagerOriginals = fileManagerArchive.Entries
            .OrderBy(entry => entry.Size)
            .Take(3)
            .ToArray();
        if (fileManagerOriginals.Length < 3)
        {
            throw new InvalidOperationException("Для проверки универсального RES-менеджера нужно не меньше трёх файлов.");
        }

        ResImportedFile importedProbe = await ResFileManagerArchive.InspectImportAsync(probeInputPath)
            .ConfigureAwait(false);
        string genericAddedName = $@"проверка\manager-{Guid.NewGuid():N}\добавлен.wav";
        string genericCopyPath = staging.GetNewOutputPath($"{Guid.NewGuid():N}-res-manager.res");
        ResFileManagerSaveResult genericCopy = await log.MeasureAsync(
            "Универсальный RES-менеджер: добавить/заменить/удалить",
            () => fileManagerArchive.SaveCopyAsync(
            [
                new ResFileEdit(ResFileEditKind.AddOrReplace, genericAddedName, importedProbe),
                new ResFileEdit(
                    ResFileEditKind.AddOrReplace,
                    fileManagerOriginals[0].Name,
                    importedProbe),
                new ResFileEdit(ResFileEditKind.Delete, fileManagerOriginals[1].Name),
            ],
            genericCopyPath)).ConfigureAwait(false);
        log.Assert(
            genericCopy.AddedCount == 1 && genericCopy.ReplacedCount == 1 && genericCopy.DeletedCount == 1 &&
            genericCopy.ResourceCount == fileManagerArchive.Entries.Count,
            "Универсальная потоковая пересборка применила ровно одно добавление, замену и удаление.");

        ResFileManagerArchive reopenedManagerCopy = await ResFileManagerArchive.OpenAsync(genericCopyPath)
            .ConfigureAwait(false);
        log.Assert(
            reopenedManagerCopy.Contains(genericAddedName) &&
            reopenedManagerCopy.Contains(fileManagerOriginals[0].Name) &&
            !reopenedManagerCopy.Contains(fileManagerOriginals[1].Name),
            "Повторно открытый RES содержит добавленный и заменённый файлы, а удалённый путь отсутствует.");
        ResArchiveDocument independentlyParsedManagerCopy = ResArchiveDocument.Open(genericCopyPath);
        SoundArchiveCatalog independentlyIndexedManagerCopy = SoundArchiveCatalog.Open([genericCopyPath]);
        log.Assert(
            independentlyParsedManagerCopy.Entries.Count == reopenedManagerCopy.Entries.Count &&
            independentlyParsedManagerCopy.Validate().All(diagnostic => !diagnostic.IsBlocking) &&
            independentlyIndexedManagerCopy.FindExact(genericAddedName).Count == 1,
            "Пересобранный RES независимо открывается общим парсером и звуковым каталогом без блокирующих ошибок.");
        string extractedAddedPath = staging.GetNewOutputPath($"{Guid.NewGuid():N}-extracted-added.wav");
        string extractedReplacementPath = staging.GetNewOutputPath($"{Guid.NewGuid():N}-extracted-replaced.wav");
        await reopenedManagerCopy.ExtractAsync(genericAddedName, extractedAddedPath).ConfigureAwait(false);
        await reopenedManagerCopy.ExtractAsync(fileManagerOriginals[0].Name, extractedReplacementPath)
            .ConfigureAwait(false);
        log.Assert(
            await FileByteComparer.AreEqualAsync(probeInputPath, extractedAddedPath).ConfigureAwait(false) &&
            await FileByteComparer.AreEqualAsync(probeInputPath, extractedReplacementPath).ConfigureAwait(false),
            "Файл с кириллическим путём и заменённый файл извлекаются из RES побайтово без изменений.");

        bool unsupportedNameRejected = false;
        try
        {
            await fileManagerArchive.SaveCopyAsync(
            [
                new ResFileEdit(
                    ResFileEditKind.AddOrReplace,
                    $@"verification\emoji-{Guid.NewGuid():N}-😀.wav",
                    importedProbe),
            ],
            staging.GetNewOutputPath($"{Guid.NewGuid():N}-unsupported-name.res")).ConfigureAwait(false);
        }
        catch (DatabaseFormatException exception) when (
            exception.Message.Contains("Windows-1251", StringComparison.OrdinalIgnoreCase))
        {
            unsupportedNameRejected = true;
        }

        log.Assert(
            unsupportedNameRejected,
            "RES-менеджер отвергает имя вне Windows-1251 и больше не заменяет символы вопросительными знаками.");

        string originalUnchangedPath = staging.GetNewOutputPath($"{Guid.NewGuid():N}-original-unchanged.bin");
        string rebuiltUnchangedPath = staging.GetNewOutputPath($"{Guid.NewGuid():N}-rebuilt-unchanged.bin");
        await fileManagerArchive.ExtractAsync(fileManagerOriginals[2].Name, originalUnchangedPath)
            .ConfigureAwait(false);
        await reopenedManagerCopy.ExtractAsync(fileManagerOriginals[2].Name, rebuiltUnchangedPath)
            .ConfigureAwait(false);
        log.Assert(
            await FileByteComparer.AreEqualAsync(originalUnchangedPath, rebuiltUnchangedPath).ConfigureAwait(false),
            "Неизменённый файл универсальной пересборки побайтово совпадает с исходным RES.");

        string managerCurrentPath = staging.GetNewOutputPath($"{Guid.NewGuid():N}-res-manager-current.res");
        File.Copy(smallestArchive.SourcePath, managerCurrentPath, overwrite: false);
        ResFileManagerArchive managerCurrent = await ResFileManagerArchive.OpenAsync(managerCurrentPath)
            .ConfigureAwait(false);
        string managerCurrentOriginalSha = ComputeFileSha256(managerCurrentPath);
        string managerCurrentAddedName = $@"verification\manager-current-{Guid.NewGuid():N}.wav";
        ResFileManagerSaveResult managerCurrentResult = await managerCurrent.SaveInPlaceAsync(
        [
            new ResFileEdit(ResFileEditKind.AddOrReplace, managerCurrentAddedName, importedProbe),
        ]).ConfigureAwait(false);
        ResFileManagerArchive reopenedManagerCurrent = await ResFileManagerArchive.OpenAsync(managerCurrentPath)
            .ConfigureAwait(false);
        log.Assert(
            reopenedManagerCurrent.Contains(managerCurrentAddedName) &&
            managerCurrentResult.BackupPath is not null &&
            File.Exists(managerCurrentResult.BackupPath) &&
            ComputeFileSha256(managerCurrentResult.BackupPath).Equals(managerCurrentOriginalSha, StringComparison.Ordinal),
            "Сохранение универсального менеджера в текущий RES создаёт точную .bak-копию и повторно открывается.");

        string managerConflictPath = staging.GetNewOutputPath($"{Guid.NewGuid():N}-res-manager-conflict.res");
        File.Copy(smallestArchive.SourcePath, managerConflictPath, overwrite: false);
        ResFileManagerArchive managerConflict = await ResFileManagerArchive.OpenAsync(managerConflictPath)
            .ConfigureAwait(false);
        File.SetLastWriteTimeUtc(managerConflictPath, managerConflict.LastWriteTimeUtc.AddSeconds(2));
        string managerConflictSha = ComputeFileSha256(managerConflictPath);
        bool managerConflictRejected = false;
        try
        {
            await managerConflict.SaveCopyAsync(
            [
                new ResFileEdit(
                    ResFileEditKind.AddOrReplace,
                    $@"verification\conflict-{Guid.NewGuid():N}.wav",
                    importedProbe),
            ],
            staging.GetNewOutputPath($"{Guid.NewGuid():N}-must-not-exist.res")).ConfigureAwait(false);
        }
        catch (IOException exception) when (exception.Message.Contains("изменился", StringComparison.OrdinalIgnoreCase))
        {
            managerConflictRejected = true;
        }

        log.Assert(
            managerConflictRejected &&
            ComputeFileSha256(managerConflictPath).Equals(managerConflictSha, StringComparison.Ordinal),
            "RES-менеджер отвергает изменившийся после открытия источник и не перезаписывает его.");

        var additionTarget = new BriefingSoundImportTarget(
            smallestArchive,
            "verification-scene",
            PhraseNumber: 1,
            probeName,
            IsReplacement: false,
            ExistingResource: null);
        string soundCopyPath = staging.GetNewOutputPath($"{Guid.NewGuid():N}-audio-edited.res");
        SoundArchiveCopyResult soundCopy = await log.MeasureAsync(
            "Потоковая копия звукового RES",
            () => SoundArchiveCopyEditor.SaveCopyAsync(additionTarget, probeAsset, soundCopyPath))
            .ConfigureAwait(false);
        log.Assert(
            File.Exists(soundCopy.TargetPath) && !soundCopy.IsReplacement && soundCopy.ReopenedSuccessfully,
            "Новый звук сохранён только в отдельной staging-копии RES.");

        SoundArchiveCatalog reopenedSoundCopy = await SoundArchiveCatalog.OpenAsync([soundCopyPath])
            .ConfigureAwait(false);
        SoundResourceInfo? reopenedProbe = reopenedSoundCopy.Resources.FirstOrDefault(resource =>
            resource.ResourceName.Equals(probeName, StringComparison.OrdinalIgnoreCase));
        log.Assert(
            reopenedProbe is not null &&
            reopenedSoundCopy.Resources.Count == smallestArchive.SoundResourceCount + 1,
            "Звуковая копия повторно разобрана и содержит ровно один новый WAV.");
        if (reopenedProbe is not null)
        {
            byte[] reopenedProbeBytes = await SoundArchiveCatalog.ReadResourceAsync(reopenedProbe)
                .ConfigureAwait(false);
            log.Assert(
                reopenedProbeBytes.AsSpan().SequenceEqual(probeAsset.Data.Span) &&
                probeAsset.Sha256.Equals(
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(reopenedProbeBytes)),
                    StringComparison.OrdinalIgnoreCase),
                "Байты и SHA-256 импортированного WAV совпадают после повторного чтения RES.");
        }

        SoundResourceInfo[] originalSmallArchiveSounds = catalog.Resources
            .Where(resource => ReferenceEquals(resource.Archive, smallestArchive))
            .ToArray();
        foreach (SoundResourceInfo originalSound in originalSmallArchiveSounds)
        {
            SoundResourceInfo copiedSound = reopenedSoundCopy.Resources.Single(resource =>
                resource.ResourceName.Equals(originalSound.ResourceName, StringComparison.OrdinalIgnoreCase));
            byte[] originalBytes = await SoundArchiveCatalog.ReadResourceAsync(originalSound).ConfigureAwait(false);
            byte[] copiedBytes = await SoundArchiveCatalog.ReadResourceAsync(copiedSound).ConfigureAwait(false);
            if (!originalBytes.AsSpan().SequenceEqual(copiedBytes))
            {
                throw new InvalidOperationException(
                    $"Неизменённый звуковой ресурс {originalSound.ResourceName} отличается в Save As.");
            }
        }

        log.Pass("Все прежние WAV/MP3/OGG в потоковой копии побайтово совпадают с исходным архивом.");

        string inPlaceSourcePath = staging.GetNewOutputPath($"{Guid.NewGuid():N}-sound-current.res");
        File.Copy(smallestArchive.SourcePath, inPlaceSourcePath, overwrite: false);
        string inPlaceOriginalSha256 = ComputeFileSha256(inPlaceSourcePath);
        SoundArchiveCatalog inPlaceCatalog = await SoundArchiveCatalog.OpenAsync([inPlaceSourcePath])
            .ConfigureAwait(false);
        SoundArchiveInfo inPlaceArchive = inPlaceCatalog.Archives.Single();
        string inPlaceProbeName = $@"s\Briefing\verification-current-{Guid.NewGuid():N}\1.wav";
        var inPlaceTarget = new BriefingSoundImportTarget(
            inPlaceArchive,
            "verification-current",
            PhraseNumber: 1,
            inPlaceProbeName,
            IsReplacement: false,
            ExistingResource: null);
        SoundArchiveCopyResult inPlaceResult = await log.MeasureAsync(
            "Безопасная замена текущего звукового RES",
            () => SoundArchiveCopyEditor.SaveInPlaceAsync(inPlaceTarget, probeAsset))
            .ConfigureAwait(false);
        log.Assert(
            inPlaceResult.TargetPath.Equals(inPlaceSourcePath, StringComparison.OrdinalIgnoreCase) &&
            inPlaceResult.BackupPath is not null && File.Exists(inPlaceResult.BackupPath) &&
            ComputeFileSha256(inPlaceResult.BackupPath).Equals(inPlaceOriginalSha256, StringComparison.Ordinal),
            "Прямая запись заменяет только временный RES и сохраняет точную проверенную .bak-копию исходника.");
        SoundArchiveCatalog reopenedInPlace = await SoundArchiveCatalog.OpenAsync([inPlaceSourcePath])
            .ConfigureAwait(false);
        SoundResourceInfo? inPlaceProbe = reopenedInPlace.Resources.FirstOrDefault(resource =>
            resource.ResourceName.Equals(inPlaceProbeName, StringComparison.OrdinalIgnoreCase));
        log.Assert(
            inPlaceProbe is not null &&
            (await SoundArchiveCatalog.ReadResourceAsync(inPlaceProbe).ConfigureAwait(false))
                .AsSpan().SequenceEqual(probeAsset.Data.Span),
            "Текущий звуковой RES повторно открыт, а добавленный WAV побайтово совпадает с импортом.");

        for (var saveNumber = 2; saveNumber <= 3; saveNumber++)
        {
            SoundArchiveCatalog nextCatalog = await SoundArchiveCatalog.OpenAsync([inPlaceSourcePath])
                .ConfigureAwait(false);
            var nextTarget = new BriefingSoundImportTarget(
                nextCatalog.Archives.Single(),
                $"verification-current-{saveNumber}",
                PhraseNumber: saveNumber,
                $@"s\Briefing\verification-current-{Guid.NewGuid():N}\{saveNumber}.wav",
                IsReplacement: false,
                ExistingResource: null);
            await SoundArchiveCopyEditor.SaveInPlaceAsync(nextTarget, probeAsset).ConfigureAwait(false);
        }

        string[] soundBackups = Directory.EnumerateFiles(
                Path.GetDirectoryName(inPlaceSourcePath)!,
                Path.GetFileName(inPlaceSourcePath) + ".bak*")
            .ToArray();
        log.Assert(
            soundBackups.Length == 2 && soundBackups.All(path => new FileInfo(path).Length > 0),
            "После трёх прямых сохранений звукового RES остаются ровно две непустые штатные backup-копии.");

        SoundArchiveCatalog beforeSoundRestore = await SoundArchiveCatalog.OpenAsync([inPlaceSourcePath])
            .ConfigureAwait(false);
        string beforeSoundRestoreSha256 = ComputeFileSha256(inPlaceSourcePath);
        ManagedResBackupInfo latestSoundBackup = ManagedResBackupManager.FindLatest(
                inPlaceSourcePath,
                ManagedResKind.Sound)
            ?? throw new InvalidOperationException("После сохранения звукового RES не найден backup.");
        ManagedResRestoreResult restoredSound = await ManagedResBackupManager.RestoreLatestAsync(
                latestSoundBackup,
                expectedCurrentSha256: beforeSoundRestoreSha256)
            .ConfigureAwait(false);
        SoundArchiveCatalog afterSoundRestore = await SoundArchiveCatalog.OpenAsync([inPlaceSourcePath])
            .ConfigureAwait(false);
        log.Assert(
            afterSoundRestore.Resources.Count == beforeSoundRestore.Resources.Count - 1 &&
            ComputeFileSha256(restoredSound.CurrentStateBackupPath).Equals(
                beforeSoundRestoreSha256,
                StringComparison.Ordinal) &&
            ManagedResBackupManager.FindLatest(inPlaceSourcePath, ManagedResKind.Sound)?.BackupCount == 2,
            "Восстановление звукового backup возвращает предыдущий RES и сохраняет точную обратную копию.");

        string changedSourcePath = staging.GetNewOutputPath($"{Guid.NewGuid():N}-sound-conflict.res");
        File.Copy(smallestArchive.SourcePath, changedSourcePath, overwrite: false);
        SoundArchiveCatalog changedCatalog = await SoundArchiveCatalog.OpenAsync([changedSourcePath])
            .ConfigureAwait(false);
        SoundArchiveInfo changedArchive = changedCatalog.Archives.Single();
        File.SetLastWriteTimeUtc(changedSourcePath, changedArchive.LastWriteTimeUtc.AddSeconds(2));
        string changedSha256 = ComputeFileSha256(changedSourcePath);
        var changedTarget = new BriefingSoundImportTarget(
            changedArchive,
            "verification-conflict",
            PhraseNumber: 1,
            $@"s\Briefing\verification-conflict-{Guid.NewGuid():N}\1.wav",
            IsReplacement: false,
            ExistingResource: null);
        bool conflictRejected = false;
        try
        {
            await SoundArchiveCopyEditor.SaveInPlaceAsync(changedTarget, probeAsset).ConfigureAwait(false);
        }
        catch (IOException exception) when (exception.Message.Contains("изменился", StringComparison.OrdinalIgnoreCase))
        {
            conflictRejected = true;
        }

        log.Assert(
            conflictRejected &&
            ComputeFileSha256(changedSourcePath).Equals(changedSha256, StringComparison.Ordinal) &&
            !Directory.EnumerateFiles(
                    Path.GetDirectoryName(changedSourcePath)!,
                    Path.GetFileName(changedSourcePath) + ".bak*")
                .Any(),
            "Изменившийся после подключения звуковой RES отклоняется до записи и без создания ложной backup-копии.");

        string textsPath = Path.Combine(directory, "texts.res");
        if (File.Exists(textsPath))
        {
            TextsDocument texts = await TextsDocument.OpenAsync(textsPath).ConfigureAwait(false);
            DatabaseTable? briefingTable = document.Databases
                .SelectMany(group => group.Tables)
                .FirstOrDefault(table => table.Key.Equals("quests.briefings", StringComparison.OrdinalIgnoreCase));
            BriefingPhrase? mappedPhrase = null;
            DatabaseSoundLink? mappedSound = null;
            if (briefingTable is not null)
            {
                foreach (DatabaseRecord record in briefingTable.Records)
                {
                    string key = string.IsNullOrWhiteSpace(record.IdentityValue)
                        ? record.Name
                        : record.IdentityValue;
                    if (!texts.TryGetResource($"BRIEFING {key}", out TextResource? resource) || resource is null)
                    {
                        continue;
                    }

                    IReadOnlyDictionary<int, BriefingPhrase> phrases = BriefingTranscriptParser
                        .Parse(resource.Description)
                        .GroupBy(phrase => phrase.Number)
                        .ToDictionary(group => group.Key, group => group.First());
                    foreach (DatabaseSoundLink link in DatabaseSoundLinkResolver.Resolve(
                                 briefingTable.Key,
                                 record,
                                 catalog))
                    {
                        if (link.Resource is not null &&
                            BriefingTranscriptParser.TryGetClipIdentity(
                                link.Resource.ResourceName,
                                out int number,
                                out _) &&
                            phrases.TryGetValue(number, out BriefingPhrase? phrase))
                        {
                            mappedPhrase = phrase;
                            mappedSound = link;
                            break;
                        }
                    }

                    if (mappedPhrase is not null)
                    {
                        break;
                    }
                }
            }

            log.Assert(
                mappedPhrase is not null && mappedSound?.Resource is not null,
                "MP3 Брифинга сопоставлен с конкретным персонажем и русским текстом из texts.res.");
            if (mappedPhrase is not null && mappedSound?.Resource is not null)
            {
                log.Info(
                    $"Пример текста MP3: {mappedSound.Resource.ResourceName} -> " +
                    $"{mappedPhrase.Speaker}: {mappedPhrase.Text}");
            }
        }

        DatabaseSoundLink? directExample = direct.FirstOrDefault(link => link.IsResolved);
        DatabaseSoundLink? briefingExample = briefings.FirstOrDefault(link => link.IsResolved);
        log.Info(
            $"Звук: archives={catalog.Archives.Count}; files={catalog.Resources.Count:N0}; " +
            $"acks={directResolved:N0}/{direct.Length:N0}; briefings={briefingResolved:N0}/{briefings.Length:N0}.");
        if (directExample?.Resource is not null)
        {
            log.Info(
                $"Пример acks.db: {directExample.Source} -> " +
                $"{directExample.Resource.Archive.FileName}/{directExample.Resource.ResourceName}");
        }

        if (briefingExample?.Resource is not null)
        {
            log.Info(
                $"Пример Брифинга: {briefingExample.Source} -> " +
                $"{briefingExample.Resource.Archive.FileName}/{briefingExample.Resource.ResourceName}");
        }

        FileSnapshot[] after = await Task.WhenAll(soundPaths.Select(path => FileSnapshot.CaptureAsync(path)))
            .ConfigureAwait(false);
        log.Assert(
            before.Zip(after).All(pair => pair.First.ContentEquals(pair.Second)),
            "Каталог, связи и потоковая Save As не изменяют исходные звуковые RES.");
    }

    private static byte[] CreateProbeWav()
    {
        byte[] pcm = [0, 0, 64, 0, 128, 0, 64, 0];
        using var stream = new MemoryStream(capacity: 44 + pcm.Length);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcm.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(8_000);
        writer.Write(16_000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();
        return stream.ToArray();
    }

    private static async Task VerifyCompanionTextsAsync(
        string databasePath,
        SafeStagingArea staging,
        VerificationLog log)
    {
        string? directory = Path.GetDirectoryName(databasePath);
        string? textsPath = directory is null ? null : Path.Combine(directory, "texts.res");
        if (textsPath is null || !File.Exists(textsPath))
        {
            log.Skip("Рядом с database.res нет texts.res; связанный текстовый контур пропущен.");
            return;
        }

        FileSnapshot sourceBefore = await FileSnapshot.CaptureAsync(textsPath).ConfigureAwait(false);
        TextsDocument document = await log.MeasureAsync(
            "Открытие связанного texts.res",
            () => TextsDocument.OpenAsync(textsPath)).ConfigureAwait(false);
        log.Assert(document.Resources.Count > 100, "texts.res содержит индексируемые текстовые ресурсы.");

        string noOpPath = staging.GetNewOutputPath($"{Guid.NewGuid():N}-texts-noop.res");
        staging.AssertOutputPath(noOpPath, [textsPath]);
        await document.SaveAsAsync(noOpPath).ConfigureAwait(false);
        log.Assert(
            await FileByteComparer.AreEqualAsync(textsPath, noOpPath).ConfigureAwait(false),
            "No-op texts.res побайтово идентичен источнику.");

        TextsDocument edited = await TextsDocument.OpenAsync(textsPath).ConfigureAwait(false);
        TextResource probe = edited.Resources.First(resource => !string.IsNullOrWhiteSpace(resource.Title));
        string editedTitle = probe.Title + " тест";
        log.Assert(probe.TrySetTitle(editedTitle, out _), "Название texts.res принимает CP1251-изменение.");
        log.Assert(
            !probe.TrySetDescription(probe.Description + " 😀", out string? encodingError) &&
            !string.IsNullOrWhiteSpace(encodingError),
            "Неподдерживаемый Windows-1251 символ отклоняется до записи.");
        string editedPath = staging.GetNewOutputPath($"{Guid.NewGuid():N}-texts-edited.res");
        staging.AssertOutputPath(editedPath, [textsPath]);
        await edited.SaveAsAsync(editedPath).ConfigureAwait(false);
        TextsDocument reopenedEdited = await TextsDocument.OpenAsync(editedPath).ConfigureAwait(false);
        log.Assert(
            reopenedEdited.TryGetResource(probe.Name, out TextResource? reopenedProbe) &&
            reopenedProbe?.Title == editedTitle,
            "Изменённое русское название сохранено и повторно прочитано.");

        TextsDocument extended = await TextsDocument.OpenAsync(textsPath).ConfigureAwait(false);
        string addedName = $"STRING EI_DATABASE_STUDIO_TEST_{Guid.NewGuid():N}";
        extended.AddResource(addedName, "Проверка", "Новая запись texts.res");
        string extendedPath = staging.GetNewOutputPath($"{Guid.NewGuid():N}-texts-added.res");
        staging.AssertOutputPath(extendedPath, [textsPath]);
        await extended.SaveAsAsync(extendedPath).ConfigureAwait(false);
        TextsDocument reopenedExtended = await TextsDocument.OpenAsync(extendedPath).ConfigureAwait(false);
        log.Assert(
            reopenedExtended.Resources.Count == document.Resources.Count + 1 &&
            reopenedExtended.TryGetResource(addedName, out TextResource? added) &&
            added?.Title == "Проверка" &&
            added.Description == "Новая запись texts.res",
            "Новый ресурс добавлен через пересобранную hash-таблицу RES и повторно прочитан.");

        FileSnapshot sourceAfter = await FileSnapshot.CaptureAsync(textsPath).ConfigureAwait(false);
        log.Assert(
            sourceBefore.ContentEquals(sourceAfter),
            "SHA-256 исходного texts.res не изменился после текстовых проверок.");
    }

    private static async Task VerifyNestedStringListDelimiterSafetyAsync(
        string sourcePath,
        VerificationLog log)
    {
        DatabaseDocument editable = await DatabaseDocument.OpenAsync(sourcePath).ConfigureAwait(false);
        DatabaseDocument reference = await DatabaseDocument.OpenAsync(sourcePath).ConfigureAwait(false);

        DatabaseTable? table = null;
        DatabaseRecord? record = null;
        DatabaseField? field = null;
        IReadOnlyList<string>? originalValues = null;
        string? nestedValue = null;
        foreach (DatabaseTable candidateTable in editable.Databases.SelectMany(group => group.Tables))
        {
            foreach (DatabaseRecord candidateRecord in candidateTable.Records)
            {
                foreach (DatabaseField candidateField in candidateRecord.Fields)
                {
                    if (!candidateField.TryGetStringListValues(out IReadOnlyList<string> values))
                    {
                        continue;
                    }

                    string? value = values.FirstOrDefault(item =>
                        item.Contains(';') &&
                        item.Contains('{') &&
                        item.Contains('}') &&
                        item.Contains('[') &&
                        item.Contains(']'));
                    if (value is null)
                    {
                        continue;
                    }

                    table = candidateTable;
                    record = candidateRecord;
                    field = candidateField;
                    originalValues = values;
                    nestedValue = value;
                    break;
                }

                if (field is not null)
                {
                    break;
                }
            }

            if (field is not null)
            {
                break;
            }
        }

        if (table is null || record is null || field is null || originalValues is null || nestedValue is null)
        {
            log.Skip("В источнике нет StringList с вложенной магией для проверки разделителей.");
            return;
        }

        DatabaseFieldState originalState = field.CaptureState();
        bool noOpAccepted = field.TrySetValue(field.EditorText, out string? noOpError);
        log.Assert(noOpAccepted, $"StringList принимает собственный EditorText: {noOpError ?? "OK"}.");
        log.Assert(
            originalState.IsEquivalentTo(field.CaptureState()),
            "Повторное применение StringList с рунами не меняет границы TLV-элементов.");

        bool malformedAccepted = field.TrySetValue(field.EditorText + "[", out string? malformedError);
        log.Assert(
            !malformedAccepted && !string.IsNullOrWhiteSpace(malformedError),
            "StringList с незакрытой скобкой отклоняется до деления текста на TLV-элементы.");
        log.Assert(
            originalState.IsEquivalentTo(field.CaptureState()),
            "Отклонённая скобка не меняет payload и границы исходного StringList.");

        int nestedIndex = originalValues.ToList().IndexOf(nestedValue);
        string[] fragments = nestedValue.Split(';', StringSplitOptions.None);
        var splitValues = new List<string>(originalValues.Count + fragments.Length - 1);
        splitValues.AddRange(originalValues.Take(nestedIndex));
        splitValues.AddRange(fragments);
        splitValues.AddRange(originalValues.Skip(nestedIndex + 1));
        log.Assert(
            field.TrySetStringListValues(splitValues, out string? splitError),
            $"Тестовое разделение StringList выполнено только в памяти: {splitError ?? "OK"}.");

        IReadOnlyList<EditorSplitStringListRepairResult> candidates =
            EditorSplitStringListRepair.FindCandidates(editable, reference);
        log.Assert(
            candidates.Any(candidate =>
                candidate.TableKey == table.Key &&
                candidate.RecordIndex == record.RecordIndex &&
                candidate.FieldId == field.Id),
            "Доказуемо разделённое редактором поле обнаруживается по чистому документу.");

        IReadOnlyList<EditorSplitStringListRepairResult> repaired =
            EditorSplitStringListRepair.Repair(editable, reference);
        log.Assert(repaired.Count == candidates.Count, "Все найденные StringList-кандидаты восстановлены в памяти.");
        log.Assert(
            originalState.IsEquivalentTo(field.CaptureState()),
            "Восстановление возвращает точные исходные TLV-границы и payload StringList.");
    }

    private static async Task VerifyCommaSeparatedStringListSafetyAsync(
        string sourcePath,
        VerificationLog log)
    {
        DatabaseDocument document = await DatabaseDocument.OpenAsync(sourcePath).ConfigureAwait(false);
        DatabaseTable? prototypes = document.Databases
            .SelectMany(group => group.Tables)
            .FirstOrDefault(table => table.Key == "units.prototypes");
        DatabaseField? armorVariants = prototypes?.Records
            .FirstOrDefault()?
            .Fields
            .FirstOrDefault(field => field.Id == 32);
        if (prototypes is null || armorVariants is null ||
            !armorVariants.TryGetStringListValues(out _))
        {
            log.Skip("В источнике нет units.prototypes/field-32 для проверки списка экипировки.");
            return;
        }

        const string joined = "vlcsh.wool,vlcgl.wool,vlcpt.wool,vlcbt.wool";
        string[] expected = ["vlcsh.wool", "vlcgl.wool", "vlcpt.wool", "vlcbt.wool"];
        log.Assert(
            armorVariants.TrySetStringListValues([joined], out string? joinedError),
            $"Тестовая склейка списка создана только в памяти: {joinedError ?? "OK"}.");
        log.Assert(
            document.Validate().Any(diagnostic =>
                diagnostic.Code == "SEM_STRING_LIST_JOINED" && diagnostic.IsBlocking),
            "Склеенная экипировка блокирует сохранение до запуска игры.");

        log.Assert(
            armorVariants.TrySetValue(joined, out string? splitError),
            $"Ввод списка через запятые принимается: {splitError ?? "OK"}.");
        log.Assert(
            armorVariants.TryGetStringListValues(out IReadOnlyList<string> splitValues) &&
            splitValues.SequenceEqual(expected),
            "Четыре предмета через запятые записываются четырьмя TLV-элементами.");
        log.Assert(
            document.Validate().All(diagnostic => diagnostic.Code != "SEM_STRING_LIST_JOINED"),
            "После разбиения предупреждение о склеенном StringList исчезает.");
    }

    private static void AssertCoreValidation(
        CoreDocumentAdapter document,
        VerificationLog log,
        string label,
        string? sourceSha256 = null)
    {
        IReadOnlyList<object> diagnostics = document.Validate();
        object[] errors = diagnostics.Where(CoreDocumentAdapter.IsErrorDiagnostic).ToArray();
        if (errors.Length > 0)
        {
            throw new VerificationException(
                $"{label}: Core вернул {errors.Length} ошибок валидации; первая: {errors[0]}");
        }

        if (label.StartsWith("Исходный", StringComparison.Ordinal))
        {
            ValidationDiagnostic[] typedDiagnostics = diagnostics
                .OfType<ValidationDiagnostic>()
                .ToArray();
            object[] notices = typedDiagnostics
                .Where(item => !CoreDocumentAdapter.IsErrorDiagnostic(item))
                .Cast<object>()
                .ToArray();
            log.Info($"{label}: предупреждений/сообщений: {notices.Length}.");
            foreach (object diagnostic in notices.Take(12))
            {
                log.Info($"{label}: {diagnostic}");
            }

            if (notices.Length > 12)
            {
                log.Info($"{label}: ещё {notices.Length - 12} сообщений не выведено.");
            }

            AssertEmbeddedMagicBaseline(typedDiagnostics, sourceSha256, log);
        }

        log.Pass($"{label}: Core-валидация не содержит ошибок.");
    }

    private static void AssertDatabaseCatalog(
        CoreDocumentAdapter document,
        IReadOnlyList<DatabaseBoundarySummary> boundaries,
        string sourceSha256,
        VerificationLog log)
    {
        IReadOnlyList<DatabaseGroup> groups = document.GetDatabases();
        DatabaseTable[] tables = groups.SelectMany(group => group.Tables).ToArray();
        string[] expectedGroupKeys =
        [
            "items",
            "levers",
            "perks",
            "prints",
            "spells",
            "units",
            "acks",
            "quests",
        ];

        log.Assert(groups.Count == expectedGroupKeys.Length, "Core-каталог содержит восемь внутренних баз.");
        log.Assert(
            groups.Select(group => group.Key).SequenceEqual(expectedGroupKeys, StringComparer.Ordinal),
            "Порядок и ключи восьми внутренних баз стабильны.");
        log.Assert(tables.Length == 26, "Core-каталог содержит 26 отдельных таблиц.");
        log.Assert(
            tables.Select(table => table.Key).Distinct(StringComparer.Ordinal).Count() == tables.Length,
            "Ключи всех таблиц уникальны.");
        log.Assert(
            groups.All(group => group.IsAvailable) && tables.All(table => table.IsAvailable),
            "Все восемь баз и 26 таблиц распознаны в источнике.");
        log.Assert(
            groups.All(group => group.SupportState == DatabaseSupportState.Editable),
            "Все восемь внутренних баз отмечены как редактируемые.");
        log.Assert(
            tables.All(table =>
                table.SupportState == DatabaseSupportState.Editable && table.CanAppendRecords),
            "Редактирование и добавление строк разрешены во всех 26 таблицах.");
        log.Assert(
            tables
                .SelectMany(table => table.Records)
                .SelectMany(record => record.Fields)
                .All(field => field.IsEditable),
            "Все физические поля всех 26 таблиц имеют редактор своего типа.");

        int boundaryRecordCount = boundaries.Sum(summary => summary.Records);
        log.Assert(
            tables.Sum(table => table.RecordCount) == boundaryRecordCount,
            $"Каталог привязал все {boundaryRecordCount:N0} записей внутренних DB к таблицам.");

        IReadOnlyList<DatabaseResourceDescriptor> resources = document.GetResources();
        string[] recognizedNames = resources
            .Where(resource => resource.IsSupported)
            .Select(resource => resource.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] catalogNames = groups
            .Select(group => group.ResourceName)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        log.Assert(
            recognizedNames.SequenceEqual(catalogNames, StringComparer.OrdinalIgnoreCase),
            "RES-каталог помечает поддерживаемыми ровно восемь распознаваемых DB-ресурсов.");
        KnownDatabaseBaseline.TryGet(sourceSha256, out KnownDatabaseBaseline? baseline);
        AssertCorrectedSchemas(tables, baseline, log);
    }

    private static void AssertEmbeddedMagicCatalog(
        IReadOnlyList<DatabaseGroup> groups,
        VerificationLog log)
    {
        EmbeddedMagicCatalog catalog = EmbeddedMagicCatalog.Create(groups.SelectMany(group => group.Tables));
        log.Assert(
            catalog.Shapes.Count > 0 && catalog.Materials.Count > 0 &&
            catalog.Spells.Count > 0 && catalog.Modifiers.Count > 0,
            "Конструктор вложенной магии получил формы, материалы, заклинания и модификаторы из database.res.");

        EmbeddedMagicShapeOption shape = catalog.Shapes[0];
        EmbeddedMagicMaterialOption? material = catalog.GetCompatibleMaterials(shape.Value).FirstOrDefault();
        EmbeddedMagicSpellOption spell = catalog.Spells[0];
        EmbeddedMagicModifierOption? itemRune = catalog.Modifiers.FirstOrDefault(modifier =>
            modifier.Value.Equals("it", StringComparison.OrdinalIgnoreCase) ||
            modifier.Value.Equals("ic", StringComparison.OrdinalIgnoreCase));
        if (material is null || itemRune is null)
        {
            log.Skip("В базе нет совместимого материала или руны it/ic для вычислительной проверки конструктора.");
            return;
        }

        EmbeddedMagicEvaluation evaluation = catalog.Evaluate(
            $"{shape.Value}.{material.Value}",
            spell.Value,
            [itemRune.Value]);
        log.Assert(
            evaluation.IsRecognized && evaluation.CanCalculate &&
            evaluation.RequiredComplexity == spell.BaseComplexity + itemRune.Complexity &&
            evaluation.AvailableComplexity == shape.Slots + material.Slots,
            "Конструктор использует ту же сумму сложности формы, материала, заклинания и рун, что семантическая проверка.");

        EmbeddedMagicEvaluation unknownModifier = catalog.Evaluate(
            $"{shape.Value}.{material.Value}",
            spell.Value,
            ["__missing_modifier__"]);
        log.Assert(
            !unknownModifier.IsAllowedForManualInsertion &&
            unknownModifier.Issues.Any(issue => issue.Contains("не найден", StringComparison.OrdinalIgnoreCase)),
            "Неизвестный модификатор блокируется конструктором до записи в RES.");
    }

    private static void AssertCorrectedSchemas(
        IReadOnlyList<DatabaseTable> tables,
        KnownDatabaseBaseline? baseline,
        VerificationLog log)
    {
        string[] anonymousHeaders = tables
            .Where(table => table.Records.Count > 0)
            .SelectMany(table => table.Records[0].Fields.SelectMany(field =>
                new[] { $"{table.Key}/field-{field.Id}/{field.DisplayName}" }
                    .Concat(field.Projections.Select(projection =>
                        $"{table.Key}/field-{field.Id}/{projection.DisplayName}"))))
            .Where(name => name.Contains("/Unk", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("/Unknown", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("/Неизвестное", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        log.Assert(
            anonymousHeaders.Length == 0,
            anonymousHeaders.Length == 0
                ? "Все ранее безымянные физические и вложенные поля получили подтверждённые названия из native-структур."
                : $"Остались безымянные поля: {string.Join(", ", anonymousHeaders.Take(12))}");

        AssertMaterialsSchema(RequireTable(tables, "items.materials"), baseline, log);
        AssertEditableItemSchema(
            RequireTable(tables, "items.weapons"),
            [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 21, 23, 24, 25, 29, 30],
            [14, 22, 26, 27, 28],
            "Оружие",
            log);
        AssertEditableItemSchema(
            RequireTable(tables, "items.armors"),
            [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 23, 24],
            [14, 21, 22, 25, 26],
            "Броня",
            log);

        DatabaseTable questItems = RequireTable(tables, "items.quest_items");
        log.Assert(
            questItems.Records.All(record =>
                new[] { 2, 3, 4 }.All(fieldId =>
                    !RequireField(record, fieldId).ShowInReadableView &&
                    RequireField(record, fieldId).IsEditable)) &&
            RequireField(questItems.Records[0], 2).DisplayName == "Служебный ID типа (не используется)" &&
            RequireField(questItems.Records[0], 3).DisplayName == "Тип материала (не используется)" &&
            RequireField(questItems.Records[0], 4).DisplayName == "Индекс типа материала (не используется)",
            "Квестовые предметы скрывают три неиспользуемых наследованных поля в понятном режиме, " +
            "но сохраняют их редактируемыми в техническом режиме.");

        DatabaseTable lootItems = RequireTable(tables, "items.loot_items");
        DatabaseField lootType = RequireField(lootItems.Records[0], 21);
        DatabaseField instructionType = RequireField(lootItems.Records[0], 22);
        DatabaseFieldProjection lootTypeProjection = lootType.Projections.Single(projection =>
            projection.Kind == DatabaseFieldProjectionKind.EnumValue);
        DatabaseFieldProjection instructionTypeProjection = instructionType.Projections.Single(projection =>
            projection.Kind == DatabaseFieldProjectionKind.EnumValue);
        IReadOnlyDictionary<int, string> expectedLootTypes = new Dictionary<int, string>
        {
            [0] = "Сокровище",
            [1] = "Материал",
            [2] = "Инструкция",
            [3] = "Сердце зелья",
            [4] = "Руна зелья",
            [int.MaxValue] = "Не применяется",
        };
        IReadOnlyDictionary<int, string> expectedInstructionTypes = new Dictionary<int, string>
        {
            [0] = "Оружие",
            [1] = "Броня",
            [2] = "Быстрый предмет",
            [3] = "Прототип заклинания",
            [4] = "Руна",
            [5] = "Сердце зелья",
            [6] = "Руна зелья",
            [int.MaxValue] = "Не применяется",
        };
        DatabaseField? unusedInstructionType = lootItems.Records
            .Select(record => RequireField(record, 22))
            .FirstOrDefault(field => Convert.ToInt32(field.Value, CultureInfo.InvariantCulture) == int.MaxValue);
        log.Assert(
            lootItems.Records.All(record =>
                !RequireField(record, 2).ShowInReadableView && RequireField(record, 2).IsEditable) &&
            lootType.DisplayName == "Тип лута" &&
            instructionType.DisplayName == "Тип инструкции" &&
            lootTypeProjection.ValueLabels is not null &&
            lootTypeProjection.ValueLabels.Count == expectedLootTypes.Count &&
            expectedLootTypes.All(pair => lootTypeProjection.ValueLabels.TryGetValue(pair.Key, out string? label) &&
                                                label == pair.Value) &&
            instructionTypeProjection.ValueLabels is not null &&
            instructionTypeProjection.ValueLabels.Count == expectedInstructionTypes.Count &&
            expectedInstructionTypes.All(pair =>
                instructionTypeProjection.ValueLabels.TryGetValue(pair.Key, out string? label) && label == pair.Value) &&
            unusedInstructionType?.GetProjectedValue(instructionTypeProjection)?.ToString() ==
                $"{int.MaxValue} — Не применяется",
            "Лут скрывает неиспользуемый ID типа и показывает полные native/addon перечисления " +
            "типов лута и инструкций, включая служебное «Не применяется».");

        DatabaseField packedAbsorption = RequireField(
            RequireTable(tables, "items.armors").Records[0],
            21);
        DatabaseFieldProjection packedPiercing = packedAbsorption.Projections[1];
        DatabaseFieldState originalPacked = packedAbsorption.CaptureState();
        string[] originalPackedValues = packedAbsorption.Projections
            .Select(projection => packedAbsorption.GetProjectedValue(projection)?.ToString() ?? string.Empty)
            .ToArray();
        float changedPacked = float.Parse(
            originalPackedValues[packedPiercing.Index],
            System.Globalization.CultureInfo.InvariantCulture) + 0.125f;
        bool packedAccepted = packedAbsorption.TrySetProjectedValue(
            packedPiercing,
            changedPacked.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            out string? packedError);
        log.Assert(
            packedAbsorption.Projections.Count == 9 &&
            packedAbsorption.Projections.All(packedAbsorption.CanEditProjection) &&
            packedAccepted && packedError is null &&
            NearlyEqual(packedAbsorption.GetProjectedValue(packedPiercing)!, changedPacked) &&
            packedAbsorption.Projections
                .Where(projection => projection.Index != packedPiercing.Index)
                .Select(projection => packedAbsorption.GetProjectedValue(projection)?.ToString() ?? string.Empty)
                .SequenceEqual(
                    originalPackedValues.Where((_, index) => index != packedPiercing.Index),
                    StringComparer.Ordinal),
            "Упакованные параметры брони редактируются по одному члену без изменения соседних 32-битных значений.");
        packedAbsorption.RestoreState(originalPacked);

        DatabaseField nestedWeather = RequireField(
            RequireTable(tables, "prints.blood").Records[0],
            2);
        DatabaseFieldProjection nestedPower = nestedWeather.Projections[0];
        DatabaseFieldState originalNested = nestedWeather.CaptureState();
        string[] originalNestedValues = nestedWeather.Projections
            .Select(projection => nestedWeather.GetProjectedValue(projection)?.ToString() ?? string.Empty)
            .ToArray();
        float changedNested = float.Parse(
            originalNestedValues[0],
            System.Globalization.CultureInfo.InvariantCulture) + 0.125f;
        bool nestedAccepted = nestedWeather.TrySetProjectedValue(
            nestedPower,
            changedNested.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            out string? nestedError);
        log.Assert(
            nestedWeather.Projections.All(nestedWeather.CanEditProjection) &&
            nestedAccepted && nestedError is null &&
            NearlyEqual(nestedWeather.GetProjectedValue(nestedPower)!, changedNested) &&
            nestedWeather.Projections.Skip(1)
                .Select(projection => nestedWeather.GetProjectedValue(projection)?.ToString() ?? string.Empty)
                .SequenceEqual(originalNestedValues.Skip(1), StringComparer.Ordinal),
            "Вложенные TLV-параметры редактируются по одному члену с сохранением заголовков и соседних чанков.");
        nestedWeather.RestoreState(originalNested);

        DatabaseField quickItemType = RequireField(
            RequireTable(tables, "items.quick_items").Records[0],
            21);
        DatabaseFieldProjection? quickItemTypeProjection = quickItemType.Projections.SingleOrDefault(projection =>
            projection.Kind == DatabaseFieldProjectionKind.EnumValue);
        if (quickItemTypeProjection is null)
        {
            throw new InvalidOperationException(
                $"У поля типа быстрого предмета отсутствует enum-проекция. Найдено: " +
                string.Join(", ", quickItemType.Projections.Select(projection =>
                    $"{projection.Kind}/{projection.DisplayName}")));
        }
        DatabaseFieldState originalEnum = quickItemType.CaptureState();
        int currentQuickItemType = Convert.ToInt32(
            quickItemType.Value,
            System.Globalization.CultureInfo.InvariantCulture);
        int changedQuickItemType = currentQuickItemType == 0 ? 1 : 0;
        string changedQuickItemLabel = quickItemTypeProjection.ValueLabels![changedQuickItemType];
        bool enumAccepted = quickItemType.TrySetProjectedValue(
            quickItemTypeProjection,
            changedQuickItemLabel,
            out string? enumError);
        string enumText = quickItemType.GetProjectedValue(quickItemTypeProjection)?.ToString() ?? string.Empty;
        bool invalidEnumAccepted = quickItemType.TrySetProjectedValue(
            quickItemTypeProjection,
            "несуществующее значение",
            out string? invalidEnumError);
        log.Assert(
            quickItemType.CanEditProjection(quickItemTypeProjection) &&
            enumAccepted && enumError is null &&
            enumText == $"{changedQuickItemType} — {changedQuickItemLabel}" &&
            !invalidEnumAccepted && !string.IsNullOrWhiteSpace(invalidEnumError) &&
            quickItemType.GetProjectedValue(quickItemTypeProjection)?.ToString() == enumText,
            "Подтверждённые enum-значения показывают номер и смысл, принимают название и отклоняют неизвестный текст.");
        quickItemType.RestoreState(originalEnum);

        DatabaseRecord hitLocation = RequireTable(tables, "units.hit_locations").Records[0];
        DatabaseRecord race = RequireTable(tables, "units.races").Records[0];
        DatabaseRecord prototype = RequireTable(tables, "units.prototypes").Records[0];
        DatabaseRecord npc = RequireTable(tables, "units.npcs").Records[0];
        log.Assert(
            RequireField(hitLocation, 4).Kind == "UInt32" &&
            RequireField(race, 1).Kind == "UInt32" &&
            RequireField(race, 45).Kind == "Byte" &&
            Enumerable.Range(47, 4).All(id => RequireField(race, id).Kind == "Int32") &&
            RequireField(prototype, 41).Kind == "Int32" &&
            RequireField(npc, 14).Kind == "Byte",
            "Типы Units соответствуют native-структурам, включая signed координаты рта и уровень.");

        DatabaseField manualSkills = RequireField(npc, 6);
        DatabaseField autoSkills = RequireField(npc, 7);
        DatabaseFieldProjection tame = manualSkills.Projections.Single(projection =>
            projection.DisplayName == "Приручение");
        DatabaseFieldState originalManualSkills = manualSkills.CaptureState();
        string[] originalManualSkillValues = manualSkills.Projections
            .Select(projection => manualSkills.GetProjectedValue(projection)?.ToString() ?? string.Empty)
            .ToArray();
        byte originalTame = byte.Parse(
            originalManualSkillValues[tame.Index],
            System.Globalization.CultureInfo.InvariantCulture);
        byte changedTame = originalTame == byte.MaxValue ? (byte)(originalTame - 1) : (byte)(originalTame + 1);
        bool tameAccepted = manualSkills.TrySetProjectedValue(
            tame,
            changedTame.ToString(System.Globalization.CultureInfo.InvariantCulture),
            out string? tameError);
        string[] changedManualSkillValues = manualSkills.Projections
            .Select(projection => manualSkills.GetProjectedValue(projection)?.ToString() ?? string.Empty)
            .ToArray();
        log.Assert(
            manualSkills.Projections.Count == 3 &&
            manualSkills.Projections.All(manualSkills.CanEditProjection) &&
            autoSkills.Projections.Count == 8 &&
            autoSkills.Projections.All(autoSkills.CanEditProjection) &&
            tameAccepted &&
            tameError is null &&
            changedManualSkillValues[tame.Index] == changedTame.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
            changedManualSkillValues.Where((_, index) => index != tame.Index).SequenceEqual(
                originalManualSkillValues.Where((_, index) => index != tame.Index),
                StringComparer.Ordinal),
            "Навыки NPC редактируются по отдельным ячейкам без изменения соседних элементов ByteList.");
        bool invalidTameAccepted = manualSkills.TrySetProjectedValue(tame, "256", out string? invalidTameError);
        log.Assert(
            !invalidTameAccepted &&
            !string.IsNullOrWhiteSpace(invalidTameError) &&
            manualSkills.GetProjectedValue(tame)?.ToString() ==
                changedTame.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "Навык NPC отклоняет значение вне диапазона 0–255 без изменения списка.");
        manualSkills.RestoreState(originalManualSkills);
        log.Assert(
            manualSkills.Projections
                .Select(projection => manualSkills.GetProjectedValue(projection)?.ToString() ?? string.Empty)
                .SequenceEqual(originalManualSkillValues, StringComparer.Ordinal) &&
            !manualSkills.IsModified,
            "После проверки исходный список навыков NPC восстановлен без следов изменения.");

        DatabaseTable spellTable = RequireTable(tables, "spells.prototypes");
        DatabaseRecord powerHit = spellTable.Records.First(record =>
            string.Equals(record.GetString("CodeName"), "powerhit", StringComparison.OrdinalIgnoreCase));
        log.Assert(
            string.Equals(powerHit.Name, RequireField(powerHit, 0).Value.ToString(), StringComparison.Ordinal) &&
            string.Equals(powerHit.GetString("CodeName"), "powerhit", StringComparison.OrdinalIgnoreCase),
            "Отображаемое имя заклинания использует поле 0 Name; CodeName остаётся отдельным ключом.");
        log.Assert(
            RequireField(powerHit, 6).Kind == "Int32" &&
            Enumerable.Range(11, 4).All(id => RequireField(powerHit, id).Kind == "Int32") &&
            RequireField(powerHit, 15).Kind == "UInt32" &&
            RequireField(powerHit, 18).Kind == "Int32" &&
            RequireField(powerHit, 19).Kind == "Int32" &&
            RequireField(powerHit, 21).Kind == "Int32",
            "SignedLong/UnsignedLong поля прототипа заклинания соответствуют dbtypes.txt.");

        log.Assert(
            RequireField(powerHit, 16).Kind == "FixedStringList" && RequireField(powerHit, 16).IsEditable &&
            RequireField(powerHit, 17).Kind == "FixedStringList" && RequireField(powerHit, 17).IsEditable &&
            RequireField(powerHit, 22).Kind == "UInt32" && RequireField(powerHit, 22).IsEditable &&
            RequireField(powerHit, 22).Projections.Count == 5,
            "MinfixedStringList(4) и BitList(5) прототипа заклинания декодируются и редактируются через исходное поле.");

        string[] lightFieldNames =
        [
            "Свет: красный",
            "Свет: зелёный",
            "Свет: синий",
            "Радиус света",
            "Время затухания",
        ];
        log.Assert(
            lightFieldNames.Select((name, index) =>
                    string.Equals(RequireField(powerHit, 23 + index).DisplayName, name, StringComparison.Ordinal))
                .All(matches => matches) &&
            RequireField(powerHit, 26).Key == "LightRadius" &&
            RequireField(powerHit, 27).Key == "FadeoutTime",
            "Поля Spells 23–27 и ключ LightRadius соответствуют dbheaders.txt.");

        DatabaseRecord modifier = RequireTable(tables, "spells.modifiers").Records[0];
        log.Assert(
            RequireField(modifier, 6).Kind == "Int32" &&
            RequireField(modifier, 8).Kind == "UInt32" &&
            RequireField(modifier, 8).Projections.Count == 5 &&
            RequireField(modifier, 8).IsEditable,
            "Модификаторы заклинаний используют SignedLong и редактируемую исходную shop-маску.");
        log.Assert(
            tables.Where(table => table.Key is
                    "spells.templates" or "spells.armor_templates" or "spells.weapon_templates")
                .SelectMany(table => table.Records)
                .All(record =>
                    RequireField(record, 5).Kind == "UInt32" &&
                    RequireField(record, 5).Projections.Count == 5 &&
                    RequireField(record, 5).IsEditable),
            "BitList(5) во всех трёх таблицах шаблонов имеет редактируемую исходную маску.");
        DatabaseField[] allFields = tables.SelectMany(table => table.Records)
            .SelectMany(record => record.Fields)
            .ToArray();
        log.Assert(
            allFields.All(field => field.Kind != "Hex"),
            $"Все {allFields.Length:N0} известных полей имеют подтверждённый тип; raw Hex не осталось.");
        string[] requiredInteractiveReferences =
        [
            "units.prototypes:1->units.races:0",
            "spells.templates:1->spells.prototypes:1",
            "spells.armor_templates:1->spells.prototypes:1",
            "spells.weapon_templates:1->spells.prototypes:1",
        ];
        string[] actualInteractiveReferences = DatabaseReferenceCatalog.ScalarReferences
            .Where(rule => requiredInteractiveReferences.Contains(
                $"{rule.SourceTableKey}:{rule.SourceFieldId}->{rule.TargetTableKey}:{rule.TargetFieldId}",
                StringComparer.Ordinal))
            .Select(rule => $"{rule.SourceTableKey}:{rule.SourceFieldId}->{rule.TargetTableKey}:{rule.TargetFieldId}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        log.Assert(
            actualInteractiveReferences.SequenceEqual(
                requiredInteractiveReferences.OrderBy(value => value, StringComparer.Ordinal),
                StringComparer.Ordinal),
            "Общий каталог ссылок содержит Base Race и три поля Prototype шаблонов магии без дублирования UI-правил.");
    }

    private static void AssertEditableItemSchema(
        DatabaseTable table,
        IReadOnlyCollection<int> editableFieldIds,
        IReadOnlyCollection<int> protectedFieldIds,
        string title,
        VerificationLog log)
    {
        log.Assert(
            table.SupportState == DatabaseSupportState.Editable && table.CanAppendRecords,
            $"{title}: таблица разрешает редактирование и клонирование строк.");
        log.Assert(
            table.Records.All(record =>
                record.Fields.Where(field => editableFieldIds.Contains(field.Id)).All(field => field.IsEditable) &&
                record.Fields.Where(field => protectedFieldIds.Contains(field.Id)).All(field => field.IsEditable)),
            $"{title}: примитивы и сложные исходные поля доступны для редактирования.");
    }

    private static void AssertMaterialsSchema(
        DatabaseTable materials,
        KnownDatabaseBaseline? baseline,
        VerificationLog log)
    {
        string[] expectedKinds =
        [
            "String",
            "String",
            "String",
            "Int32",
            "Float32",
            "Float32",
            "Float32",
            "Int32",
            "Float32",
            "Int32",
            "Float32",
            "Float32List",
            "Int32",
            "UInt32",
        ];

        log.Assert(materials.Records.Count > 0, "Items / Материалы содержит записи.");
        if (baseline is not null)
        {
            log.Assert(
                materials.Records.Count == baseline.MaterialCount,
                $"Контент-база «{baseline.Name}» содержит ровно {baseline.MaterialCount} материалов.");
        }
        else
        {
            log.Skip("Для неизвестного SHA-256 не проверяется точное число материалов; структура проверяется полностью.");
        }
        log.Assert(
            materials.SupportState == DatabaseSupportState.Editable &&
            materials.CanAppendRecords &&
            materials.Records.All(record => record.Fields.All(field => field.IsEditable)),
            "Материалы разрешают редактирование всех физических полей и добавление строк.");
        log.Assert(
            materials.Records.All(record =>
                record.Fields.Count == expectedKinds.Length &&
                record.Fields.Select(field => field.Id).SequenceEqual(Enumerable.Range(0, expectedKinds.Length)) &&
                record.Fields.Select(field => field.Kind).SequenceEqual(expectedKinds, StringComparer.Ordinal)),
            $"Все {materials.Records.Count:N0} материалов имеют точную последовательность и типы полей 0–13 из legacy-схемы.");
        DatabaseField absorption = RequireField(materials.Records[0], 11);
        DatabaseField shops = RequireField(materials.Records[0], 13);
        log.Assert(
            absorption.Projections.Count == 7 &&
            absorption.Projections.Select(item => item.Kind)
                .All(kind => kind == DatabaseFieldProjectionKind.ListElement) &&
            absorption.Projections.Select(item => item.Index).SequenceEqual(Enumerable.Range(0, 7)) &&
            absorption.Projections.Select(item => item.DisplayName).SequenceEqual(
                ["Колющий", "Рубящий", "Дробящий", "Термический", "Химический", "Электрический", "Общий"],
                StringComparer.Ordinal) &&
            shops.Projections.Count == 5 &&
            shops.Projections.Select(item => item.Kind).All(kind => kind == DatabaseFieldProjectionKind.Bit) &&
            shops.Projections.Select(item => item.Index).SequenceEqual(Enumerable.Range(0, 5)),
            "FloatList и BitList Материалов разворачиваются в 12 проверенных виртуальных колонок.");

        DatabaseFieldProjection shop1 = shops.Projections[0];
        DatabaseFieldProjection shop2 = shops.Projections[1];
        DatabaseFieldProjection piercing = absorption.Projections[0];
        DatabaseFieldState originalShops = shops.CaptureState();
        DatabaseFieldState originalAbsorption = absorption.CaptureState();
        uint originalShopMask = Convert.ToUInt32(
            shops.Value,
            System.Globalization.CultureInfo.InvariantCulture);
        string toggledShop1 = (originalShopMask & 1u) == 0 ? "1" : "0";
        bool shop1Accepted = shops.TrySetProjectedValue(shop1, toggledShop1, out string? shop1Error);
        uint changedShopMask = Convert.ToUInt32(
            shops.Value,
            System.Globalization.CultureInfo.InvariantCulture);
        log.Assert(
            shops.CanEditProjection(shop1) &&
            shops.CanEditProjection(shop2) &&
            absorption.Projections.All(absorption.CanEditProjection),
            "Битовые и числовые проекции объявлены редактируемыми.");
        log.Assert(
            shop1Accepted &&
            shop1Error is null &&
            changedShopMask == (originalShopMask ^ 1u) &&
            (changedShopMask & 2u) == (originalShopMask & 2u),
            "Редактирование Shop 1 меняет ровно один бит и сохраняет соседний Shop 2.");
        bool invalidShopAccepted = shops.TrySetProjectedValue(shop1, "2", out string? invalidShopError);
        log.Assert(
            !invalidShopAccepted &&
            !string.IsNullOrWhiteSpace(invalidShopError) &&
            Convert.ToUInt32(shops.Value, System.Globalization.CultureInfo.InvariantCulture) == changedShopMask,
            "Битовая проекция отклоняет значения вне 0/1, не меняя маску.");
        string[] originalAbsorptionValues = absorption.Projections
            .Select(projection => absorption.GetProjectedValue(projection)?.ToString() ?? string.Empty)
            .ToArray();
        float changedPiercing = float.Parse(
            originalAbsorptionValues[0],
            System.Globalization.CultureInfo.InvariantCulture) + 0.25f;
        bool piercingAccepted = absorption.TrySetProjectedValue(
            piercing,
            changedPiercing.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ','),
            out string? piercingError);
        string[] changedAbsorptionValues = absorption.Projections
            .Select(projection => absorption.GetProjectedValue(projection)?.ToString() ?? string.Empty)
            .ToArray();
        log.Assert(
            piercingAccepted &&
            piercingError is null &&
            NearlyEqual(
                absorption.GetProjectedValue(piercing)!,
                changedPiercing) &&
            changedAbsorptionValues.Skip(1).SequenceEqual(originalAbsorptionValues.Skip(1), StringComparer.Ordinal),
            "Редактирование одного элемента Float32List принимает десятичную запятую и сохраняет соседние значения.");
        bool invalidPiercingAccepted = absorption.TrySetProjectedValue(
            piercing,
            "не число",
            out string? invalidPiercingError);
        log.Assert(
            !invalidPiercingAccepted &&
            !string.IsNullOrWhiteSpace(invalidPiercingError) &&
            NearlyEqual(absorption.GetProjectedValue(piercing)!, changedPiercing),
            "Числовая проекция отклоняет неверное значение без частичного изменения списка.");
        shops.RestoreState(originalShops);
        absorption.RestoreState(originalAbsorption);
        log.Assert(
            Convert.ToUInt32(shops.Value, System.Globalization.CultureInfo.InvariantCulture) == originalShopMask &&
            !shops.IsModified &&
            absorption.Projections
                .Select(projection => absorption.GetProjectedValue(projection)?.ToString() ?? string.Empty)
                .SequenceEqual(originalAbsorptionValues, StringComparer.Ordinal) &&
            !absorption.IsModified,
            "После проверки исходные shop-маска и числовой список восстановлены без следов изменения.");
        log.Assert(
            materials.Records.All(record =>
                RequireField(record, 11).Length == 28 &&
                Enumerable.Range(3, 8).All(id => RequireField(record, id).Length == 4) &&
                RequireField(record, 12).Length == 4 &&
                RequireField(record, 13).Length == 4),
            "Размеры payload Материалов соответствуют 7 float и little-endian 32-bit scalars.");

        if (baseline is not null)
        {
            DatabaseRecord thin = materials.Records[0];
            log.Assert(
                string.Equals(thin.Name, "thin", StringComparison.Ordinal) &&
                string.Equals(RequireField(thin, 1).Value.ToString(), "Leather", StringComparison.Ordinal) &&
                string.Equals(RequireField(thin, 2).Value.ToString(), "le", StringComparison.Ordinal) &&
                Convert.ToInt32(RequireField(thin, 3).Value, System.Globalization.CultureInfo.InvariantCulture) == 0 &&
                NearlyEqual(RequireField(thin, 4).Value, 4f) &&
                NearlyEqual(RequireField(thin, 5).Value, 1.2f) &&
                NearlyEqual(RequireField(thin, 6).Value, 11f) &&
                Convert.ToInt32(RequireField(thin, 7).Value, System.Globalization.CultureInfo.InvariantCulture) == 6 &&
                NearlyEqual(RequireField(thin, 8).Value, 32.94f) &&
                Convert.ToInt32(RequireField(thin, 9).Value, System.Globalization.CultureInfo.InvariantCulture) == 1 &&
                NearlyEqual(RequireField(thin, 10).Value, 0f) &&
                string.Equals(
                    RequireField(thin, 11).Value.ToString(),
                    "1.14, 1.2, 1.23, 1.14, 1.2, 1.35, 1.2",
                    StringComparison.Ordinal) &&
                Convert.ToInt32(RequireField(thin, 12).Value, System.Globalization.CultureInfo.InvariantCulture) == 0 &&
                Convert.ToUInt32(RequireField(thin, 13).Value, System.Globalization.CultureInfo.InvariantCulture) == 1,
                "Первая строка thin декодируется в те же понятные значения, что и старый DBEditor.");

            DatabaseRecord last = materials.Records[^1];
            log.Assert(
                string.Equals(last.Name, "black panter hide", StringComparison.Ordinal) &&
                string.Equals(RequireField(last, 1).Value.ToString(), "Fur", StringComparison.Ordinal) &&
                string.Equals(RequireField(last, 2).Value.ToString(), "pt", StringComparison.Ordinal) &&
                Convert.ToInt32(RequireField(last, 3).Value, System.Globalization.CultureInfo.InvariantCulture) == 49 &&
                Convert.ToUInt32(RequireField(last, 13).Value, System.Globalization.CultureInfo.InvariantCulture) == 0,
                "Последняя строка black panter hide корректно декодируется до конца таблицы.");

            uint[] allowedShopMasks = [0, 1, 4, 8, 12, 16];
            log.Assert(
                materials.Records.All(record =>
                    Convert.ToInt32(RequireField(record, 12).Value, System.Globalization.CultureInfo.InvariantCulture) == 0 &&
                    allowedShopMasks.Contains(
                        Convert.ToUInt32(RequireField(record, 13).Value, System.Globalization.CultureInfo.InvariantCulture))),
                "Маска травм и пять shop-битов всех материалов декодируются без старших неизвестных битов.");
        }
    }

    private static void AssertEmbeddedMagicBaseline(
        IReadOnlyList<ValidationDiagnostic> diagnostics,
        string? sourceSha256,
        VerificationLog log)
    {
        static int CountCode(IEnumerable<ValidationDiagnostic> source, string code) =>
            source.Count(diagnostic => diagnostic.Code.Equals(code, StringComparison.Ordinal));

        int itemFormat = CountCode(diagnostics, "SEM_EMBEDDED_MAGIC_FORMAT");
        int spellFormat = diagnostics.Count(diagnostic =>
            diagnostic.Code.Equals("SEM_VALUE_FORMAT", StringComparison.Ordinal) &&
            diagnostic.Location?.TableKey.Equals("units.prototypes", StringComparison.Ordinal) == true &&
            diagnostic.Location.FieldId == 29);
        int missingEmbeddedRunes = diagnostics.Count(diagnostic =>
            diagnostic.Code.Equals("SEM_REFERENCE_NOT_FOUND", StringComparison.Ordinal) &&
            diagnostic.Location?.TableKey.Equals("units.prototypes", StringComparison.Ordinal) == true &&
            diagnostic.Location.FieldId == 29 &&
            diagnostic.Message.Contains("модификатор", StringComparison.OrdinalIgnoreCase));
        int tooManyRunes = CountCode(diagnostics, "SEM_EMBEDDED_MAGIC_TOO_MANY_RUNES");
        int energyExceeded = CountCode(diagnostics, "SEM_EMBEDDED_MAGIC_ENERGY_EXCEEDED");
        int complexityExceeded = CountCode(diagnostics, "SEM_EMBEDDED_MAGIC_COMPLEXITY_EXCEEDED");
        int materialMismatch = CountCode(diagnostics, "SEM_ITEM_MATERIAL_TYPE_MISMATCH");
        int missingMaterial = CountCode(diagnostics, "SEM_EMBEDDED_MAGIC_MATERIAL_NOT_FOUND");
        int missingItemRune = CountCode(diagnostics, "SEM_EMBEDDED_MAGIC_ITEM_RUNE_REQUIRED");
        int namedEmbeddedDiagnostics = diagnostics.Count(diagnostic =>
            diagnostic.Code.StartsWith("SEM_EMBEDDED_MAGIC_", StringComparison.Ordinal));
        int magicRelated = namedEmbeddedDiagnostics + materialMismatch + spellFormat + missingEmbeddedRunes;

        if (!KnownDatabaseBaseline.TryGet(sourceSha256, out KnownDatabaseBaseline? baseline) || baseline is null)
        {
            log.Skip(
                $"Для SHA-256 {sourceSha256 ?? "<не задан>"} нет контент-базовой линии; " +
                "точные количества семантических сообщений не проверяются.");
            return;
        }

        log.Assert(
            diagnostics.Count == baseline.TotalDiagnostics && diagnostics.All(diagnostic => !diagnostic.IsBlocking),
            $"Контент-база «{baseline.Name}» имеет ровно {baseline.TotalDiagnostics} неблокирующих сообщений.");
        log.Assert(
            itemFormat == baseline.ItemFormat && spellFormat == baseline.SpellFormat &&
            missingEmbeddedRunes == baseline.MissingEmbeddedRunes,
            $"Вложенная магия: {baseline.ItemFormat} ошибок item-скобок, " +
            $"{baseline.SpellFormat} spell-скобок и {baseline.MissingEmbeddedRunes} неизвестных рун.");
        log.Assert(
            tooManyRunes == baseline.TooManyRunes && energyExceeded == baseline.EnergyExceeded &&
            complexityExceeded == baseline.ComplexityExceeded,
            $"Вложенная магия: {baseline.TooManyRunes} записей содержат >8 рун, " +
            $"{baseline.EnergyExceeded} превышают энергию и {baseline.ComplexityExceeded} превышают сложность.");
        log.Assert(
            materialMismatch == baseline.MaterialMismatch && missingMaterial == baseline.MissingMaterial &&
            missingItemRune == baseline.MissingItemRune && magicRelated == baseline.MagicRelated,
            $"Вложенная магия: {baseline.MaterialMismatch} несовместимых типов материала, " +
            $"{baseline.MissingMaterial} пропавших материалов, {baseline.MissingItemRune} без ic/it; " +
            $"всего {baseline.MagicRelated} сообщений.");
    }

    private static async Task VerifyRowAppendSafetyAsync(
        string sourcePath,
        IReadOnlyCollection<string> allSourcePaths,
        SafeStagingArea staging,
        ResArchiveDocument sourceArchive,
        int sourceRecordCount,
        VerificationLog log)
    {
        await VerifyMaterialAppendSafetyAsync(
            sourcePath,
            allSourcePaths,
            staging,
            sourceArchive,
            sourceRecordCount,
            log).ConfigureAwait(false);
        await VerifyItemAppendSafetyAsync(
            sourcePath,
            allSourcePaths,
            staging,
            sourceArchive,
            sourceRecordCount,
            "items.weapons",
            "оружия",
            log).ConfigureAwait(false);
        await VerifyItemAppendSafetyAsync(
            sourcePath,
            allSourcePaths,
            staging,
            sourceArchive,
            sourceRecordCount,
            "items.armors",
            "брони",
            log).ConfigureAwait(false);
        await VerifyMonsterAppendSafetyAsync(
            sourcePath,
            allSourcePaths,
            staging,
            sourceArchive,
            sourceRecordCount,
            log).ConfigureAwait(false);
    }

    private static async Task VerifyComplexFieldRoundTripAsync(
        string sourcePath,
        IReadOnlyCollection<string> allSourcePaths,
        SafeStagingArea staging,
        VerificationLog log)
    {
        DatabaseDocument document = await DatabaseDocument.OpenAsync(sourcePath).ConfigureAwait(false);
        DatabaseTable[] tables = document.Databases.SelectMany(group => group.Tables).ToArray();
        string[] kinds =
        [
            "Float32List",
            "UInt32List",
            "ByteList",
            "StringList",
            "FixedStringList",
            "Nested",
            "StructuredList",
            "PackedStructure",
        ];
        var probes = new List<ComplexEditProbe>();
        foreach (string kind in kinds)
        {
            ComplexEditProbe? probe = tables
                .SelectMany(table => table.Records.Select(record => (Table: table, Record: record)))
                .SelectMany(item => item.Record.Fields.Select(field =>
                    new ComplexEditProbe(item.Table.Key, item.Record.RecordIndex, field.Id, field.Kind, string.Empty)))
                .FirstOrDefault(candidate =>
                {
                    if (!candidate.Kind.Equals(kind, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    DatabaseRecord record = RequireTable(tables, candidate.TableKey).Records[candidate.RecordIndex];
                    DatabaseField field = RequireField(record, candidate.FieldId);
                    return field.IsEditable && field.EditorText.Length > 0;
                });
            if (probe is null)
            {
                continue;
            }

            DatabaseField target = RequireField(
                RequireTable(tables, probe.TableKey).Records[probe.RecordIndex],
                probe.FieldId);
            string editedText = CreateComplexEditText(target);
            SetFieldValue(target, editedText, $"round-trip {kind}");
            probes.Add(probe with { ExpectedText = target.EditorText });
        }

        log.Assert(
            probes.Select(probe => probe.Kind).SequenceEqual(kinds, StringComparer.Ordinal),
            "Найдены и изменены представители всех восьми сложных типов полей.");
        log.Assert(
            document.Validate().All(diagnostic => !diagnostic.IsBlocking),
            "Изменения сложных полей не создают структурных блокировок Save As.");

        string targetPath = staging.GetNewOutputPath($"{Guid.NewGuid():N}-all-complex-types.res");
        staging.AssertOutputPath(targetPath, allSourcePaths);
        await document.SaveAsAsync(targetPath).ConfigureAwait(false);
        DatabaseDocument reopened = await DatabaseDocument.OpenAsync(targetPath).ConfigureAwait(false);
        DatabaseTable[] reopenedTables = reopened.Databases.SelectMany(group => group.Tables).ToArray();
        log.Assert(
            probes.All(probe =>
                RequireField(
                        RequireTable(reopenedTables, probe.TableKey).Records[probe.RecordIndex],
                        probe.FieldId)
                    .EditorText.Equals(probe.ExpectedText, StringComparison.Ordinal)),
            "Все восемь сложных типов сохранились и повторно открылись с точными изменёнными значениями.");
        _ = DatabaseBoundaryVerifier.Verify(ResArchiveDocument.Open(targetPath));
        log.Assert(
            reopened.Validate().All(diagnostic => !diagnostic.IsBlocking),
            "Копия после записи всех сложных типов повторно разобрана без блокирующих ошибок.");
    }

    private static async Task VerifyDatabaseComparisonAsync(
        string sourcePath,
        VerificationLog log)
    {
        DatabaseDocument current = await DatabaseDocument.OpenAsync(sourcePath).ConfigureAwait(false);
        DatabaseDocument compared = await DatabaseDocument.OpenAsync(sourcePath).ConfigureAwait(false);

        DatabaseComparisonResult identical = DatabaseComparer.Compare(current, compared);
        log.Assert(
            identical.AreEquivalent && identical.Differences.Count == 0,
            "Сравнение двух одинаковых database.res не создаёт ложных различий.");

        (DatabaseTable Table, DatabaseRecord Record, DatabaseField Field) editableProbe = compared.Databases
            .SelectMany(group => group.Tables)
            .SelectMany(table => table.Records.Select(record => (Table: table, Record: record)))
            .SelectMany(item => item.Record.Fields.Select(field => (item.Table, item.Record, Field: field)))
            .First(item =>
                item.Field.IsEditable &&
                item.Field.Kind.Equals("String", StringComparison.Ordinal) &&
                item.Field.Id != item.Record.IdentityFieldId &&
                item.Field.EditorText.Length < 128);
        string editedValue = editableProbe.Field.EditorText + "_comparison_probe";
        SetFieldValue(editableProbe.Field, editedValue, "проверка сравнения баз");

        DatabaseComparisonResult changed = DatabaseComparer.Compare(current, compared);
        DatabaseDifference? changedField = changed.Differences.SingleOrDefault(difference =>
            difference.Kind == DatabaseDifferenceKind.FieldChanged &&
            difference.TableKey.Equals(editableProbe.Table.Key, StringComparison.Ordinal) &&
            difference.CurrentRecordIndex == editableProbe.Record.RecordIndex &&
            difference.FieldId == editableProbe.Field.Id);
        log.Assert(
            changed.Differences.Count == 1 &&
            changedField is not null &&
            changedField.ComparedValue.Equals(editedValue, StringComparison.Ordinal),
            "Сравнение находит одно точное изменение физического поля и показывает оба значения.");

        DatabaseTable appendableTable = compared.Databases
            .SelectMany(group => group.Tables)
            .First(table => table.CanAppendRecords && table.Records.Count > 0);
        DatabaseRecord added = compared.AppendRecordClone(appendableTable.Records[0]);
        DatabaseField identityField = RequireField(added, added.IdentityFieldId);
        SetFieldValue(
            identityField,
            $"codex_comparison_{Guid.NewGuid():N}",
            "уникальная запись сравнения");

        DatabaseComparisonResult withAddedRecord = DatabaseComparer.Compare(current, compared);
        log.Assert(
            withAddedRecord.OnlyInComparedCount == 1 &&
            withAddedRecord.Differences.Any(difference =>
                difference.Kind == DatabaseDifferenceKind.OnlyInCompared &&
                difference.TableKey.Equals(appendableTable.Key, StringComparison.Ordinal)),
            "Сравнение отдельно показывает запись, существующую только во второй базе.");
    }

    private static async Task VerifyStructuredAckListRoundTripAsync(
        string sourcePath,
        IReadOnlyCollection<string> allSourcePaths,
        SafeStagingArea staging,
        VerificationLog log)
    {
        DatabaseDocument document = await DatabaseDocument.OpenAsync(sourcePath).ConfigureAwait(false);
        DatabaseTable table = RequireTable(document.Databases.SelectMany(group => group.Tables), "acks.answers");
        (DatabaseRecord Record, DatabaseField Field) probe = table.Records
            .SelectMany(record => record.Fields.Select(field => (Record: record, Field: field)))
            .First(item =>
                item.Field.CanEditStructuredList &&
                item.Field.TryGetStructuredListEntries(out _, out IReadOnlyList<StructuredListEntryValue> entries) &&
                entries.Any(entry =>
                    entry.Fields.TryGetValue(2, out string? probability) &&
                    int.TryParse(probability, out _)));

        string originalHex = probe.Field.EditorText;
        log.Assert(
            probe.Field.TryGetStructuredListEntries(
                out IReadOnlyList<StructuredListFieldDescriptor> descriptors,
                out IReadOnlyList<StructuredListEntryValue> originalEntries) &&
            new[] { 1, 2, 3, 4 }.All(id => descriptors.Any(field => field.Id == id && field.IsEditable)),
            "Структурная реплика acks.db раскрывает редактируемые поля «Файл», «Вероятность», «Описание» и «Ключ».");

        int entryIndex = originalEntries.ToList().FindIndex(entry =>
            entry.Fields.TryGetValue(2, out string? probability) && int.TryParse(probability, out _));
        var editedEntries = originalEntries
            .Select(entry => new StructuredListEntryValue(
                entry.Index,
                new Dictionary<int, string>(entry.Fields)))
            .ToArray();
        var editedFields = (Dictionary<int, string>)editedEntries[entryIndex].Fields;
        int originalProbability = int.Parse(editedFields[2], CultureInfo.InvariantCulture);
        string editedProbability = checked(originalProbability + 1).ToString(CultureInfo.InvariantCulture);
        string editedDescription = editedFields.GetValueOrDefault(3, string.Empty) + " тест";
        editedFields[2] = editedProbability;
        editedFields[3] = editedDescription;

        log.Assert(
            probe.Field.TrySetStructuredListEntries(editedEntries, out string? editError),
            $"Понятное поле «Вероятность» принято без ручного редактирования hex: {editError}");
        log.Assert(
            probe.Field.TryGetStructuredListEntries(out _, out IReadOnlyList<StructuredListEntryValue> decodedEdited) &&
            decodedEdited[entryIndex].Fields[2] == editedProbability &&
            decodedEdited[entryIndex].Fields[3] == editedDescription,
            "Изменённые вероятность и CP1251-описание немедленно читаются обратно через структурный список.");
        log.Assert(
            probe.Field.TrySetStructuredListEntries(originalEntries, out string? restoreError) &&
            probe.Field.EditorText.Equals(originalHex, StringComparison.Ordinal),
            $"Возврат исходных понятных значений побайтово восстанавливает составное поле: {restoreError}");

        log.Assert(
            probe.Field.TrySetStructuredListEntries(editedEntries, out editError),
            $"Повторное структурное изменение подготовлено для Save As: {editError}");
        string targetPath = staging.GetNewOutputPath($"{Guid.NewGuid():N}-acks-structured-list.res");
        staging.AssertOutputPath(targetPath, allSourcePaths);
        await document.SaveAsAsync(targetPath).ConfigureAwait(false);

        DatabaseDocument reopened = await DatabaseDocument.OpenAsync(targetPath).ConfigureAwait(false);
        DatabaseField reopenedField = RequireField(
            RequireTable(reopened.Databases.SelectMany(group => group.Tables), "acks.answers")
                .Records[probe.Record.RecordIndex],
            probe.Field.Id);
        log.Assert(
            reopenedField.TryGetStructuredListEntries(out _, out IReadOnlyList<StructuredListEntryValue> reopenedEntries) &&
            reopenedEntries[entryIndex].Fields[2] == editedProbability &&
            reopenedEntries[entryIndex].Fields[3] == editedDescription,
            "Понятные числовое и строковое изменения acks.db сохранились в тестовую копию и повторно открылись.");
        log.Assert(
            reopened.Validate().All(diagnostic => !diagnostic.IsBlocking),
            "Копия с изменённым структурным списком acks.db не содержит блокирующих ошибок.");
        _ = DatabaseBoundaryVerifier.Verify(ResArchiveDocument.Open(targetPath));
        log.Pass("Структурный список acks.db повторно разобран до границ всех TLV-полей.");
    }

    private static string CreateComplexEditText(DatabaseField field)
    {
        string[] values = field.EditorText.Split(
            ',',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return field.Kind switch
        {
            "Float32List" => string.Join(", ", values.Select((value, index) => index == 0
                ? (float.Parse(value, System.Globalization.CultureInfo.InvariantCulture) + 0.125f)
                    .ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                : value)),
            "UInt32List" => string.Join(", ", values.Select((value, index) => index == 0
                ? (uint.Parse(value, System.Globalization.CultureInfo.InvariantCulture) ^ 1U)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture)
                : value)),
            "ByteList" => string.Join(", ", values.Select((value, index) => index == 0
                ? ((byte)(byte.Parse(value, System.Globalization.CultureInfo.InvariantCulture) ^ 1))
                    .ToString(System.Globalization.CultureInfo.InvariantCulture)
                : value)),
            "StringList" => field.EditorText + "; codex_probe",
            "FixedStringList" => "zz",
            "Nested" or "StructuredList" or "PackedStructure" => MutateRawHex(field.EditorText),
            _ => throw new InvalidOperationException($"Нет complex-edit probe для {field.Kind}."),
        };
    }

    private static string MutateRawHex(string text)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(text);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"Raw-редактор вернул не hex: {text[..Math.Min(text.Length, 80)]}",
                exception);
        }
        bytes[^1] ^= 1;
        return Convert.ToHexString(bytes);
    }

    private sealed record ComplexEditProbe(
        string TableKey,
        int RecordIndex,
        int FieldId,
        string Kind,
        string ExpectedText);

    private static async Task VerifyItemAppendSafetyAsync(
        string sourcePath,
        IReadOnlyCollection<string> allSourcePaths,
        SafeStagingArea staging,
        ResArchiveDocument sourceArchive,
        int sourceRecordCount,
        string tableKey,
        string title,
        VerificationLog log)
    {
        DatabaseDocument document = await DatabaseDocument.OpenAsync(sourcePath).ConfigureAwait(false);
        DatabaseTable table = RequireTable(document.Databases.SelectMany(group => group.Tables), tableKey);
        int originalCount = table.RecordCount;
        DatabaseRecord template = table.Records[0];
        DatabaseRawRecord rawTemplate = DatabaseBoundaryVerifier.CaptureRecord(
            sourceArchive,
            template.ResourceName,
            template.SectionId,
            template.RecordIndex);

        DatabaseRecord added = document.AppendRecordClone(template);
        log.Assert(
            table.CanAppendRecords && added.IsNew && added.RecordIndex == originalCount &&
            table.RecordCount == originalCount + 1,
            $"Клон {title} добавлен строго в конец таблицы.");

        DatabaseField identityField = RequireField(added, added.IdentityFieldId);
        string uniqueIdentity = $"codex_{tableKey.Replace('.', '_')}_{Guid.NewGuid():N}";
        SetFieldValue(identityField, uniqueIdentity, $"уникальный ключ {title}");
        DatabaseField editedPriceField = RequireField(added, 7);
        float editedPrice = Convert.ToSingle(
            editedPriceField.Value,
            System.Globalization.CultureInfo.InvariantCulture) + 1f;
        SetFieldValue(
            editedPriceField,
            editedPrice.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"изменённая цена {title}");
        log.Assert(
            document.Validate().All(diagnostic => !diagnostic.IsBlocking),
            $"Уникальный ключ разрешает безопасное сохранение нового {title}.");

        string savedTarget = staging.GetNewOutputPath($"{Guid.NewGuid():N}-{tableKey.Replace('.', '-')}-added.res");
        staging.AssertOutputPath(savedTarget, allSourcePaths);
        await document.SaveAsAsync(savedTarget).ConfigureAwait(false);

        DatabaseDocument reopened = await DatabaseDocument.OpenAsync(savedTarget).ConfigureAwait(false);
        DatabaseTable reopenedTable = RequireTable(reopened.Databases.SelectMany(group => group.Tables), tableKey);
        DatabaseRecord reopenedAdded = reopenedTable.Records.Single(record =>
            record.IdentityValue.Equals(uniqueIdentity, StringComparison.Ordinal));
        log.Assert(
            reopenedTable.RecordCount == originalCount + 1 &&
            reopenedAdded.RecordIndex == originalCount && !reopenedAdded.IsNew &&
            NearlyEqual(RequireField(reopenedAdded, 7).Value, editedPrice),
            $"Новый объект {title} сохранён последней записью вместе с изменённой ценой.");

        ResArchiveDocument savedArchive = ResArchiveDocument.Open(savedTarget);
        AssertResourceIdentity(sourceArchive, savedArchive, "items.idb", log);
        log.Assert(
            DatabaseBoundaryVerifier.Verify(savedArchive).Sum(summary => summary.Records) == sourceRecordCount + 1,
            $"Добавление {title} увеличило число физических DB-записей ровно на одну.");
        DatabaseRawRecord rawAdded = DatabaseBoundaryVerifier.CaptureRecord(
            savedArchive,
            reopenedAdded.ResourceName,
            reopenedAdded.SectionId,
            reopenedAdded.RecordIndex);
        AssertClonedRawFields(rawTemplate, rawAdded, [(byte)template.IdentityFieldId, 7], title, log);
    }

    private static async Task VerifyMaterialAppendSafetyAsync(
        string sourcePath,
        IReadOnlyCollection<string> allSourcePaths,
        SafeStagingArea staging,
        ResArchiveDocument sourceArchive,
        int sourceRecordCount,
        VerificationLog log)
    {
        DatabaseDocument document = await log.MeasureAsync(
            "Открытие для добавления материала",
            () => DatabaseDocument.OpenAsync(sourcePath)).ConfigureAwait(false);
        DatabaseTable materials = RequireTable(
            document.Databases.SelectMany(group => group.Tables),
            "items.materials");
        int originalCount = materials.RecordCount;
        DatabaseRecord template = materials.Records[0];
        DatabaseRawRecord rawTemplate = DatabaseBoundaryVerifier.CaptureRecord(
            sourceArchive,
            template.ResourceName,
            template.SectionId,
            template.RecordIndex);

        log.Assert(
            materials.CanAppendRecords && materials.SupportState == DatabaseSupportState.Editable,
            "Core разрешает append только для проверенной редактируемой таблицы Материалы.");
        DatabaseRecord added = document.AppendRecordClone(template);
        log.Assert(
            added.IsNew && added.RecordIndex == originalCount &&
            materials.RecordCount == originalCount + 1 &&
            ReferenceEquals(materials.Records[^1], added),
            "Клон материала добавлен строго в конец и получил следующий физический индекс.");
        log.Assert(
            Convert.ToInt32(RequireField(added, 3).Value, System.Globalization.CultureInfo.InvariantCulture) ==
            originalCount,
            "ID нового материала автоматически равен его физическому индексу.");
        log.Assert(
            RequireField(template, 11).Kind == "Float32List" && RequireField(template, 11).IsEditable,
            "Образец материала содержит редактируемый список поглощения для побайтовой проверки клона.");

        string duplicateTarget = staging.GetNewOutputPath($"{Guid.NewGuid():N}-material-duplicate.res");
        staging.AssertOutputPath(duplicateTarget, allSourcePaths);
        AssertIdentitySaveBlocked(document, duplicateTarget, "ADD_IDENTITY_DUPLICATE", log);

        DatabaseField identityField = RequireField(added, added.IdentityFieldId);
        SetFieldValue(identityField, string.Empty, "пустой ключ нового материала");
        log.Assert(added.IdentityValue.Length == 0, "Тестовый ключ нового материала очищен только в памяти.");
        string emptyTarget = staging.GetNewOutputPath($"{Guid.NewGuid():N}-material-empty.res");
        staging.AssertOutputPath(emptyTarget, allSourcePaths);
        AssertIdentitySaveBlocked(document, emptyTarget, "ADD_IDENTITY_REQUIRED", log);

        SetFieldValue(identityField, $"codex_material_guard_{Guid.NewGuid():N}", "временный уникальный ключ материала");
        DatabaseField materialIdField = RequireField(added, 3);
        SetFieldValue(materialIdField, "0", "неверный физический ID нового материала");
        string invalidIdTarget = staging.GetNewOutputPath($"{Guid.NewGuid():N}-material-invalid-id.res");
        staging.AssertOutputPath(invalidIdTarget, allSourcePaths);
        AssertIdentitySaveBlocked(document, invalidIdTarget, "ADD_MATERIAL_ID_MISMATCH", log);
        SetFieldValue(
            materialIdField,
            originalCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "восстановление физического ID нового материала");
        log.Assert(
            document.Validate().All(diagnostic => !diagnostic.IsBlocking),
            "Возврат ID нового материала к физическому индексу снимает блокировку.");

        document.RemoveAddedRecord(added);
        log.Assert(
            materials.RecordCount == originalCount && !document.IsModified,
            "Undo удаления добавленной строки вернул исходное число материалов и no-op состояние.");
        string undoneTarget = staging.GetNewOutputPath($"{Guid.NewGuid():N}-material-undone.res");
        staging.AssertOutputPath(undoneTarget, allSourcePaths);
        await document.SaveAsAsync(undoneTarget).ConfigureAwait(false);
        log.Assert(
            await FileByteComparer.AreEqualAsync(sourcePath, undoneTarget).ConfigureAwait(false),
            "Save As после удаления добавленной строки побайтово идентичен источнику.");

        document.RestoreAddedRecord(added);
        log.Assert(
            document.IsModified && materials.RecordCount == originalCount + 1 &&
            ReferenceEquals(materials.Records[^1], added),
            "Redo восстановил тот же клон материала в конец таблицы.");

        string uniqueIdentity = $"codex_material_{Guid.NewGuid():N}";
        SetFieldValue(identityField, uniqueIdentity, "уникальный ключ нового материала");
        log.Assert(
            document.Validate().All(diagnostic => !diagnostic.IsBlocking),
            "Уникальный ключ снимает блокировку сохранения нового материала.");

        string savedTarget = staging.GetNewOutputPath($"{Guid.NewGuid():N}-material-added.res");
        staging.AssertOutputPath(savedTarget, allSourcePaths);
        await log.MeasureAsync(
            "Save As с новым материалом",
            () => document.SaveAsAsync(savedTarget)).ConfigureAwait(false);

        DatabaseDocument reopened = await DatabaseDocument.OpenAsync(savedTarget).ConfigureAwait(false);
        DatabaseTable reopenedMaterials = RequireTable(
            reopened.Databases.SelectMany(group => group.Tables),
            "items.materials");
        DatabaseRecord reopenedAdded = reopenedMaterials.Records.Single(record =>
            record.IdentityValue.Equals(uniqueIdentity, StringComparison.Ordinal));
        log.Assert(
            reopenedMaterials.RecordCount == originalCount + 1 &&
            reopenedAdded.RecordIndex == originalCount && !reopenedAdded.IsNew &&
            Convert.ToInt32(RequireField(reopenedAdded, 3).Value, System.Globalization.CultureInfo.InvariantCulture) ==
            originalCount,
            "Новый материал сохранён, повторно открыт в конце и сохранил физический ID.");
        log.Assert(
            reopened.Validate().All(diagnostic => !diagnostic.IsBlocking),
            "Повторно открытая копия с новым материалом не содержит блокирующих ошибок.");

        ResArchiveDocument savedArchive = ResArchiveDocument.Open(savedTarget);
        AssertResourceIdentity(sourceArchive, savedArchive, "items.idb", log);
        log.Assert(
            DatabaseBoundaryVerifier.Verify(savedArchive).Sum(summary => summary.Records) == sourceRecordCount + 1,
            "Добавление материала увеличило число физических DB-записей ровно на одну.");
        DatabaseRawRecord rawAdded = DatabaseBoundaryVerifier.CaptureRecord(
            savedArchive,
            reopenedAdded.ResourceName,
            reopenedAdded.SectionId,
            reopenedAdded.RecordIndex);
        AssertClonedRawFields(rawTemplate, rawAdded, [0, 3], "материала", log);
    }

    private static async Task VerifyMonsterAppendSafetyAsync(
        string sourcePath,
        IReadOnlyCollection<string> allSourcePaths,
        SafeStagingArea staging,
        ResArchiveDocument sourceArchive,
        int sourceRecordCount,
        VerificationLog log)
    {
        DatabaseDocument document = await log.MeasureAsync(
            "Открытие для добавления прототипа монстра",
            () => DatabaseDocument.OpenAsync(sourcePath)).ConfigureAwait(false);
        DatabaseTable prototypes = RequireTable(
            document.Databases.SelectMany(group => group.Tables),
            "units.prototypes");
        int originalCount = prototypes.RecordCount;
        string[] monsterMarkers = ["troll", "orc", "goblin", "dragon", "undead"];
        DatabaseRecord template = prototypes.Records.FirstOrDefault(record =>
                monsterMarkers.Any(marker => record.Name.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            ?? prototypes.Records[0];
        DatabaseRawRecord rawTemplate = DatabaseBoundaryVerifier.CaptureRecord(
            sourceArchive,
            template.ResourceName,
            template.SectionId,
            template.RecordIndex);

        DatabaseRecord added = document.AppendRecordClone(template);
        log.Assert(
            prototypes.CanAppendRecords && added.IsNew && added.RecordIndex == originalCount &&
            prototypes.RecordCount == originalCount + 1,
            $"Прототип монстра клонирован в конец units.prototypes из образца «{template.Name}».");
        log.Assert(
            template.Fields.Any(field => field.Kind is "Nested" or "StringList" or "Float32List"),
            "Образец монстра содержит сложные поля для побайтовой проверки клона.");

        DatabaseField identityField = RequireField(added, added.IdentityFieldId);
        string uniqueIdentity = $"codex_monster_{Guid.NewGuid():N}";
        SetFieldValue(identityField, uniqueIdentity, "уникальный ключ нового монстра");
        log.Assert(
            document.Validate().All(diagnostic => !diagnostic.IsBlocking),
            "Уникальный ключ разрешает безопасное сохранение нового прототипа монстра.");

        string savedTarget = staging.GetNewOutputPath($"{Guid.NewGuid():N}-monster-added.res");
        staging.AssertOutputPath(savedTarget, allSourcePaths);
        await log.MeasureAsync(
            "Save As с новым прототипом монстра",
            () => document.SaveAsAsync(savedTarget)).ConfigureAwait(false);

        DatabaseDocument reopened = await DatabaseDocument.OpenAsync(savedTarget).ConfigureAwait(false);
        DatabaseTable reopenedPrototypes = RequireTable(
            reopened.Databases.SelectMany(group => group.Tables),
            "units.prototypes");
        DatabaseRecord reopenedAdded = reopenedPrototypes.Records.Single(record =>
            record.IdentityValue.Equals(uniqueIdentity, StringComparison.Ordinal));
        log.Assert(
            reopenedPrototypes.RecordCount == originalCount + 1 &&
            reopenedAdded.RecordIndex == originalCount && !reopenedAdded.IsNew,
            "Новый прототип монстра сохранён и повторно открыт последней физической записью.");
        log.Assert(
            reopened.Validate().All(diagnostic => !diagnostic.IsBlocking),
            "Повторно открытая копия с новым монстром не содержит блокирующих ошибок.");

        ResArchiveDocument savedArchive = ResArchiveDocument.Open(savedTarget);
        AssertResourceIdentity(sourceArchive, savedArchive, "units.udb", log);
        log.Assert(
            DatabaseBoundaryVerifier.Verify(savedArchive).Sum(summary => summary.Records) == sourceRecordCount + 1,
            "Добавление монстра увеличило число физических DB-записей ровно на одну.");
        DatabaseRawRecord rawAdded = DatabaseBoundaryVerifier.CaptureRecord(
            savedArchive,
            reopenedAdded.ResourceName,
            reopenedAdded.SectionId,
            reopenedAdded.RecordIndex);
        AssertClonedRawFields(
            rawTemplate,
            rawAdded,
            [(byte)template.IdentityFieldId],
            "прототипа монстра",
            log);
    }

    private static void AssertIdentitySaveBlocked(
        DatabaseDocument document,
        string targetPath,
        string expectedCode,
        VerificationLog log)
    {
        ValidationDiagnostic[] matching = document.Validate()
            .Where(diagnostic => diagnostic.Code.Equals(expectedCode, StringComparison.Ordinal))
            .ToArray();
        log.Assert(
            matching.Length == 1 && matching[0].IsBlocking,
            $"Проверка {expectedCode} создаёт одну блокирующую диагностику для новой строки.");

        Exception? caught = null;
        try
        {
            document.SaveAs(targetPath);
        }
        catch (Exception exception)
        {
            caught = exception;
        }

        log.Assert(
            caught is InvalidOperationException &&
            caught.Message.Contains(expectedCode, StringComparison.Ordinal),
            $"Save As отклонён до записи файла с явной причиной {expectedCode}.");
        log.Assert(
            !File.Exists(targetPath) &&
            !Directory.EnumerateFiles(
                    Path.GetDirectoryName(targetPath)!,
                    $".{Path.GetFileName(targetPath)}.partial-*")
                .Any(),
            "Заблокированный Save As не оставил целевой или partial-файл.");
    }

    private static void SetFieldValue(DatabaseField field, string value, string label)
    {
        if (!field.TrySetValue(value, out string? error))
        {
            throw new VerificationException(
                $"Core отклонил {label} в поле {field.Path}: {error ?? "причина не указана"}.");
        }
    }

    private static void AssertClonedRawFields(
        DatabaseRawRecord template,
        DatabaseRawRecord clone,
        IReadOnlyCollection<byte> changedFieldIds,
        string label,
        VerificationLog log)
    {
        if (template.Fields.Count != clone.Fields.Count)
        {
            throw new VerificationException(
                $"Raw-структура клона {label} отличается: fields {template.Fields.Count}->{clone.Fields.Count}.");
        }

        var compared = 0;
        for (var index = 0; index < template.Fields.Count; index++)
        {
            DatabaseRawField expected = template.Fields[index];
            DatabaseRawField actual = clone.Fields[index];
            if (expected.Id != actual.Id)
            {
                throw new VerificationException(
                    $"Клон {label}: поле #{index} изменило ID {expected.Id}->{actual.Id}.");
            }

            if (changedFieldIds.Contains(expected.Id))
            {
                continue;
            }

            if (expected.HeaderSize != actual.HeaderSize ||
                !expected.Payload.AsSpan().SequenceEqual(actual.Payload))
            {
                throw new VerificationException(
                    $"Клон {label}: неизменённый payload поля {expected.Id} отличается от образца.");
            }

            compared++;
        }

        if (changedFieldIds.Any(id => template.Fields.All(field => field.Id != id)))
        {
            throw new VerificationException($"Клон {label}: изменяемое поле отсутствовало в исходном образце.");
        }

        log.Pass(
            $"Клон {label}: {compared} неизменённых полей, включая сложные/read-only payload, " +
            "сохранены побайтово вместе с типом и размером заголовка поля; " +
            $"внешний заголовок записи корректно имеет размер {clone.HeaderSize} байт.");
    }

    private static bool NearlyEqual(object value, float expected) =>
        Math.Abs(Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture) - expected) <= 0.0001f;

    private static string ComputeFileSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static DatabaseTable RequireTable(
        IEnumerable<DatabaseTable> tables,
        string key) =>
        tables.FirstOrDefault(table => string.Equals(table.Key, key, StringComparison.Ordinal))
        ?? throw new VerificationException($"Core-каталог не содержит таблицу {key}.");

    private static DatabaseField RequireField(DatabaseRecord record, int id) =>
        record.Fields.FirstOrDefault(field => field.Id == id)
        ?? throw new VerificationException($"Запись {record.Path} не содержит поле {id}.");

    private static void AssertResourceIdentity(
        ResArchiveDocument source,
        ResArchiveDocument output,
        string? changedResourceName,
        VerificationLog log)
    {
        if (source.Entries.Count != output.Entries.Count)
        {
            throw new VerificationException(
                $"Число ресурсов изменилось: {source.Entries.Count} -> {output.Entries.Count}.");
        }

        var unchangedCount = 0;
        var changedResourceObserved = false;
        foreach (ResEntry sourceEntry in source.Entries)
        {
            if (!output.TryGetEntry(sourceEntry.Name, out ResEntry? outputEntry) || outputEntry is null)
            {
                throw new VerificationException($"Ресурс исчез после сохранения: {sourceEntry.Name}.");
            }

            bool identical = source.GetResourceBytes(sourceEntry).Span.SequenceEqual(
                output.GetResourceBytes(outputEntry).Span);
            if (string.Equals(sourceEntry.Name, changedResourceName, StringComparison.OrdinalIgnoreCase))
            {
                changedResourceObserved = !identical;
                continue;
            }

            if (!identical)
            {
                throw new VerificationException(
                    $"Неизменённый ресурс отличается после сохранения: {sourceEntry.Name}.");
            }

            unchangedCount++;
        }

        if (changedResourceName is not null && !changedResourceObserved)
        {
            throw new VerificationException(
                $"Ожидалось изменение ресурса {changedResourceName}, но его байты совпали.");
        }

        log.Pass(
            changedResourceName is null
                ? $"Все {unchangedCount} ресурсов побайтово идентичны источнику."
                : $"Все {unchangedCount} незатронутых ресурсов побайтово идентичны источнику; " +
                  $"изменился только {changedResourceName}.");
    }

    private static void AssertResourceResize(
        ResArchiveDocument source,
        ResArchiveDocument output,
        string changedResourceName,
        VerificationLog log)
    {
        if (!source.TryGetEntry(changedResourceName, out ResEntry? sourceChanged) || sourceChanged is null ||
            !output.TryGetEntry(changedResourceName, out ResEntry? outputChanged) || outputChanged is null)
        {
            throw new VerificationException($"Не найден изменяемый ресурс {changedResourceName}.");
        }

        int resourceDelta = outputChanged.Size - sourceChanged.Size;
        int archiveDelta = output.Length - source.Length;
        int directoryDelta = output.DirectoryOffset - source.DirectoryOffset;
        if (resourceDelta != 1 || archiveDelta != 1 || directoryDelta != 1)
        {
            throw new VerificationException(
                $"Ожидалось расширение на 1 байт: resource={resourceDelta}, " +
                $"archive={archiveDelta}, directory={directoryDelta}.");
        }

        bool laterOffsetShifted = source.Entries
            .Where(entry => entry.Offset > sourceChanged.Offset)
            .Any(entry =>
                output.TryGetEntry(entry.Name, out ResEntry? outputEntry) &&
                outputEntry is not null &&
                outputEntry.Offset - entry.Offset == 1);
        if (!laterOffsetShifted)
        {
            throw new VerificationException(
                "После расширения spells.sdb не обнаружено обновлённого смещения следующего ресурса.");
        }

        log.Pass(
            "Variable-length edit увеличил spells.sdb и RES на 1 байт; " +
            "смещения последующих ресурсов и каталога обновлены.");
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "Использование:\n" +
            "  dotnet run --project tests/EvilIslands.DatabaseEditor.Verification -- [параметры]\n\n" +
            "Параметры:\n" +
            "  --source <path>     Проверить указанный RES; параметр можно повторять.\n" +
            "  --require-sources   Вернуть ошибку, если ни один источник не найден.\n" +
            "  --repair-source <path>     Восстановить доказуемо разделённые StringList в TEMP-копии.\n" +
            "  --repair-reference <path>  Указать чистую опорную базу для режима восстановления.\n" +
            "  --help, -h          Показать эту справку.");
    }
}
