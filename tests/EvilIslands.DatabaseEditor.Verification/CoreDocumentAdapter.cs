using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using EvilIslands.DatabaseEditor.Core;

namespace EvilIslands.DatabaseEditor.Verification;

internal sealed record EditProbe(
    string ResourceName,
    string RecordCodeName,
    string FieldKey,
    string OriginalValue,
    string EditedValue);

internal sealed class CoreDocumentAdapter
{
    private const string DocumentTypeName = "EvilIslands.DatabaseEditor.Core.DatabaseDocument";
    private readonly object document;

    private CoreDocumentAdapter(object document)
    {
        this.document = document;
    }

    public static async Task<CoreDocumentAdapter> OpenAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        Type documentType = typeof(ResArchiveDocument).Assembly.GetType(
            DocumentTypeName,
            throwOnError: true,
            ignoreCase: false)!;

        object? opened = await InvokePathOperationAsync(
            target: null,
            documentType,
            "OpenAsync",
            path,
            cancellationToken).ConfigureAwait(false);

        if (opened is null)
        {
            MethodInfo? open = documentType.GetMethod(
                "Open",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [typeof(string)],
                modifiers: null);
            opened = open?.Invoke(null, [path]);
        }

        return new CoreDocumentAdapter(
            opened ?? throw new MissingMethodException(DocumentTypeName, "Open/OpenAsync"));
    }

    public bool ContainsPowerHit()
    {
        object? spellsDatabase = GetProperty(document, "Spells");
        return Enumerate(GetProperty(spellsDatabase, "Spells"))
            .Any(record => string.Equals(GetRecordString(record, "CodeName"), "powerhit", StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<DatabaseGroup> GetDatabases() =>
        Enumerate(GetProperty(document, "Databases")).Cast<DatabaseGroup>().ToArray();

    public IReadOnlyList<DatabaseResourceDescriptor> GetResources() =>
        Enumerate(GetProperty(document, "Resources")).Cast<DatabaseResourceDescriptor>().ToArray();

    public IReadOnlyList<object> Validate()
    {
        MethodInfo? method = document.GetType().GetMethod(
            "Validate",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        object? result = method?.Invoke(document, null);
        if (result is null)
        {
            result = GetProperty(document, "Diagnostics");
        }

        object? nested = GetProperty(result, "Diagnostics", "Messages", "Items");
        return Enumerate(nested ?? result).ToArray();
    }

    public static bool IsErrorDiagnostic(object diagnostic)
    {
        string severity = GetProperty(diagnostic, "Severity", "Level")?.ToString() ?? string.Empty;
        return severity.Equals("Error", StringComparison.OrdinalIgnoreCase);
    }

    public EditProbe ApplyPowerHitScalarEdit()
    {
        object spellsDatabase = GetProperty(document, "Spells")
            ?? throw new VerificationException("Core не предоставил декодированную базу Spells.");
        object powerHit = Enumerate(GetProperty(spellsDatabase, "Spells"))
            .FirstOrDefault(record =>
                string.Equals(GetRecordString(record, "CodeName"), "powerhit", StringComparison.OrdinalIgnoreCase))
            ?? throw new VerificationException("Запись powerhit не найдена для тестового изменения.");

        string[] preferredFields =
        [
            "Price",
            "BaseManaCost",
            "Speed",
            "Range",
            "Area",
            "Effect",
            "LightRadius",
            "LightIntensity",
        ];

        object[] fields = Enumerate(GetProperty(powerHit, "Fields")).ToArray();
        foreach (string fieldKey in preferredFields)
        {
            object? field = fields.FirstOrDefault(candidate =>
                string.Equals(GetProperty(candidate, "Key", "Name")?.ToString(), fieldKey, StringComparison.OrdinalIgnoreCase));
            if (field is null || GetProperty(field, "IsEditable", "CanEdit") is false)
            {
                continue;
            }

            object? currentValue = GetProperty(field, "Value");
            if (!TryCreateEditedText(currentValue, out string? editedText))
            {
                continue;
            }

            string originalText = FormatValue(currentValue);
            MethodInfo? setter = field.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(method =>
                    method.Name.Equals("TrySetValue", StringComparison.Ordinal) &&
                    method.GetParameters().Length == 2);
            if (setter is null)
            {
                continue;
            }

            object?[] arguments = [editedText, null];
            bool accepted = setter.Invoke(field, arguments) is true;
            if (!accepted)
            {
                continue;
            }

            string canonicalEdited = FormatValue(GetProperty(field, "Value"));
            if (string.Equals(originalText, canonicalEdited, StringComparison.Ordinal))
            {
                throw new VerificationException($"Поле {fieldKey} приняло изменение, но значение не изменилось.");
            }

            return new EditProbe("spells.sdb", "powerhit", fieldKey, originalText, canonicalEdited);
        }

        throw new VerificationException("Для powerhit не найдено безопасное числовое поле с TrySetValue.");
    }

    public EditProbe ApplyPowerHitVariableLengthStringEdit()
    {
        object spellsDatabase = GetProperty(document, "Spells")
            ?? throw new VerificationException("Core не предоставил декодированную базу Spells.");
        object powerHit = Enumerate(GetProperty(spellsDatabase, "Spells"))
            .FirstOrDefault(record =>
                string.Equals(GetRecordString(record, "CodeName"), "powerhit", StringComparison.OrdinalIgnoreCase))
            ?? throw new VerificationException("Запись powerhit не найдена для строкового теста.");
        object nameField = Enumerate(GetProperty(powerHit, "Fields"))
            .FirstOrDefault(candidate =>
                string.Equals(GetProperty(candidate, "Key", "Name")?.ToString(), "Name", StringComparison.OrdinalIgnoreCase))
            ?? throw new VerificationException("Поле powerhit.Name не найдено.");

        if (GetProperty(nameField, "IsEditable", "CanEdit") is false)
        {
            throw new VerificationException("Поле powerhit.Name недоступно для редактирования.");
        }

        string originalText = GetProperty(nameField, "Value")?.ToString()
            ?? throw new VerificationException("Поле powerhit.Name не является строкой.");
        string editedText = originalText + "я";

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding windows1251 = Encoding.GetEncoding(
            1251,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        int byteDelta = windows1251.GetByteCount(editedText) - windows1251.GetByteCount(originalText);
        if (byteDelta != 1)
        {
            throw new VerificationException(
                $"Строковая проба должна увеличивать CP1251 payload ровно на байт, получено: {byteDelta}.");
        }

        MethodInfo setter = nameField.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method =>
                method.Name.Equals("TrySetValue", StringComparison.Ordinal) &&
                method.GetParameters().Length == 2)
            ?? throw new VerificationException("Для powerhit.Name отсутствует TrySetValue.");
        object?[] arguments = [editedText, null];
        if (setter.Invoke(nameField, arguments) is not true)
        {
            throw new VerificationException(
                $"Core отклонил CP1251-строку для powerhit.Name: {arguments[1]}");
        }

        string canonicalEdited = FormatValue(GetProperty(nameField, "Value"));
        if (!string.Equals(canonicalEdited, editedText, StringComparison.Ordinal))
        {
            throw new VerificationException(
                $"Core вернул неожиданное значение powerhit.Name: '{canonicalEdited}'.");
        }

        return new EditProbe("spells.sdb", "powerhit", "Name", originalText, canonicalEdited);
    }

    public string GetPowerHitFieldValue(string fieldKey)
    {
        object? spellsDatabase = GetProperty(document, "Spells");
        object powerHit = Enumerate(GetProperty(spellsDatabase, "Spells"))
            .FirstOrDefault(record =>
                string.Equals(GetRecordString(record, "CodeName"), "powerhit", StringComparison.OrdinalIgnoreCase))
            ?? throw new VerificationException("Запись powerhit отсутствует после повторного открытия.");
        object field = Enumerate(GetProperty(powerHit, "Fields"))
            .FirstOrDefault(candidate =>
                string.Equals(GetProperty(candidate, "Key", "Name")?.ToString(), fieldKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new VerificationException($"Поле powerhit.{fieldKey} отсутствует после повторного открытия.");

        return FormatValue(GetProperty(field, "Value"));
    }

    public async Task SaveAsAsync(string path, CancellationToken cancellationToken = default)
    {
        object? result = await InvokePathOperationAsync(
            document,
            document.GetType(),
            "SaveAsAsync",
            path,
            cancellationToken).ConfigureAwait(false);
        if (result is not null)
        {
            return;
        }

        MethodInfo? save = document.GetType().GetMethod(
            "SaveAs",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: [typeof(string)],
            modifiers: null);
        if (save is null)
        {
            throw new MissingMethodException(document.GetType().FullName, "SaveAs/SaveAsAsync");
        }

        save.Invoke(document, [path]);
    }

    private static string? GetRecordString(object record, string key)
    {
        MethodInfo? getter = record.GetType().GetMethod(
            "GetString",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: [typeof(string)],
            modifiers: null);
        if (getter?.Invoke(record, [key]) is string value)
        {
            return value;
        }

        object? field = Enumerate(GetProperty(record, "Fields"))
            .FirstOrDefault(candidate =>
                string.Equals(GetProperty(candidate, "Key", "Name")?.ToString(), key, StringComparison.OrdinalIgnoreCase));
        return GetProperty(field, "Value")?.ToString();
    }

    private static bool TryCreateEditedText(object? value, out string? editedText)
    {
        editedText = value switch
        {
            float number when float.IsFinite(number) => (number + 1.0f).ToString("R", CultureInfo.InvariantCulture),
            double number when double.IsFinite(number) => (number + 1.0d).ToString("R", CultureInfo.InvariantCulture),
            int number when number < int.MaxValue => (number + 1).ToString(CultureInfo.InvariantCulture),
            uint number when number < uint.MaxValue => (number + 1).ToString(CultureInfo.InvariantCulture),
            long number when number < long.MaxValue => (number + 1).ToString(CultureInfo.InvariantCulture),
            ulong number when number < ulong.MaxValue => (number + 1).ToString(CultureInfo.InvariantCulture),
            _ => null,
        };

        return editedText is not null;
    }

    private static async Task<object?> InvokePathOperationAsync(
        object? target,
        Type type,
        string methodName,
        string path,
        CancellationToken cancellationToken)
    {
        BindingFlags flags = BindingFlags.Public |
            (target is null ? BindingFlags.Static : BindingFlags.Instance);
        MethodInfo[] methods = type.GetMethods(flags)
            .Where(method => method.Name.Equals(methodName, StringComparison.Ordinal))
            .OrderByDescending(method =>
                method.GetParameters().Any(parameter => parameter.ParameterType == typeof(CancellationToken)))
            .ToArray();

        foreach (MethodInfo method in methods)
        {
            object?[]? arguments = BuildArguments(method.GetParameters(), path, cancellationToken);
            if (arguments is null)
            {
                continue;
            }

            object? invocation = method.Invoke(target, arguments);
            if (invocation is Task task)
            {
                await task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)?.GetValue(task)
                    ?? VoidTaskResult.Instance;
            }

            return invocation ?? VoidTaskResult.Instance;
        }

        return null;
    }

    private static object?[]? BuildArguments(
        ParameterInfo[] parameters,
        string path,
        CancellationToken cancellationToken)
    {
        var arguments = new object?[parameters.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            ParameterInfo parameter = parameters[index];
            if (parameter.ParameterType == typeof(string))
            {
                arguments[index] = path;
            }
            else if (parameter.ParameterType == typeof(CancellationToken))
            {
                arguments[index] = cancellationToken;
            }
            else if (parameter.HasDefaultValue)
            {
                arguments[index] = parameter.DefaultValue;
            }
            else
            {
                return null;
            }
        }

        return arguments;
    }

    private static object? GetProperty(object? source, params string[] names)
    {
        if (source is null)
        {
            return null;
        }

        foreach (string name in names)
        {
            PropertyInfo? property = source.GetType().GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property is not null)
            {
                return property.GetValue(source);
            }
        }

        return null;
    }

    private static IEnumerable<object> Enumerate(object? source)
    {
        if (source is null || source is string || source is not IEnumerable values)
        {
            yield break;
        }

        foreach (object? value in values)
        {
            if (value is not null)
            {
                yield return value;
            }
        }
    }

    private static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty,
    };

    private sealed class VoidTaskResult
    {
        public static readonly VoidTaskResult Instance = new();
    }
}
