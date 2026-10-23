using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioWorks.Application;
using Umu;

namespace PortfolioWorks.Infrastructure;

public sealed class UmuCurriculumProvider(
    StudentService.StudentServiceClient client,
    IOptions<UmuOptions> options,
    ILogger<UmuCurriculumProvider> logger)
    : IStudentCurriculumProvider
{
    // Коды из Umu.proto. Нужны только как запасной вариант: если сервер
    // проигнорирует include и вернёт *_id без расшифровки.
    private static readonly Dictionary<int, string> ObjectTypeNames = new()
    {
        [1] = "Модули",
        [2] = "Дисциплины",
        [3] = "Практики",
        [4] = "Контроль",
        [5] = "Блоки по выбору",
        [6] = "ГИА"
    };

    private static readonly Dictionary<int, string> PracticeTypeNames = new()
    {
        [6] = "Учебная",
        [7] = "Производственная",
        [8] = "НИР",
        [72] = "Преддипломная"
    };

    public async Task<IReadOnlyCollection<StudyGroup>> GetCurriculumAsync(string? studentEmail, int? studentId,
        bool includeGrades, CancellationToken cancellationToken)
    {
        var authorization = options.Value.Authorization;
        if (string.IsNullOrWhiteSpace(authorization))
            throw new InvalidOperationException("Umu:Authorization is not configured.");

        var headers = new Metadata { { "authorization", authorization } };
        var records = await LoadStudentRecordsAsync(studentEmail, studentId, headers, cancellationToken);

        if (records.Count == 0)
        {
            // Профиль пуст или недоступен: не теряем дисциплины, отдаём их
            // одной группой без названия.
            var grades = includeGrades
                ? await LoadGradesAsync(BuildStudentRequest(studentEmail, studentId), headers, cancellationToken)
                : null;
            var disciplines = await LoadDisciplinesAsync(BuildStudentRequest(studentEmail, studentId), headers,
                grades, cancellationToken);
            return disciplines.Count == 0
                ? []
                : [new StudyGroup(studentId ?? 0, 0, string.Empty, string.Empty, string.Empty, disciplines)];
        }

        var groups = new List<StudyGroup>(records.Count);
        foreach (var record in records)
        {
            // Запрашиваем план именно по записи (student_id), без email: иначе
            // УМУ может вернуть план другой записи этого же человека.
            var grades = includeGrades
                ? await LoadGradesAsync(new StudentRequest { StudentId = record.Id }, headers, cancellationToken)
                : null;
            var disciplines = await LoadDisciplinesAsync(new StudentRequest { StudentId = record.Id }, headers,
                grades, cancellationToken);
            var (groupId, groupName) = ResolveGroup(record);
            groups.Add(new StudyGroup(record.Id, groupId, groupName, ResolveSpecialtyName(record),
                ResolveLevelName(record), disciplines));
        }

        return groups;
    }

    private async Task<IReadOnlyList<StudentRecord>> LoadStudentRecordsAsync(string? studentEmail, int? studentId,
        Metadata headers, CancellationToken cancellationToken)
    {
        try
        {
            return await GetStudentRecordsAsync(BuildStudentRequest(studentEmail, studentId, includeProfileNames: true),
                headers, cancellationToken);
        }
        catch (RpcException exception) when (exception.StatusCode is StatusCode.InvalidArgument or StatusCode.Unimplemented)
        {
            // УМУ может не знать include для профиля. Без них останутся только
            // *_id, но по записям всё равно можно разбить дисциплины.
            logger.LogWarning(exception, "Umu rejected profile includes; retrying without them.");
            return await GetStudentRecordsAsync(BuildStudentRequest(studentEmail, studentId), headers,
                cancellationToken);
        }
        catch (RpcException exception) when (exception.StatusCode != StatusCode.Cancelled)
        {
            // Группировка — дополнительная информация: если профиль недоступен,
            // отдаём план без разбиения, а не роняем весь запрос.
            logger.LogWarning(exception, "Failed to load Umu student profile; curriculum will not be grouped.");
            return [];
        }
    }

    private async Task<IReadOnlyList<StudentRecord>> GetStudentRecordsAsync(StudentRequest request, Metadata headers,
        CancellationToken cancellationToken)
    {
        var response = await client.GetStudentProfileAsync(request, headers, cancellationToken: cancellationToken);
        return response.Students.ToArray();
    }

    private async Task<IReadOnlyCollection<Discipline>> LoadDisciplinesAsync(StudentRequest request, Metadata headers,
        IReadOnlyDictionary<string, DisciplineGrade>? grades, CancellationToken cancellationToken)
    {
        // Расшифровки нужны, чтобы отличить практику от дисциплины и понять её вид.
        request.Include.Add("ObjectType");
        request.Include.Add("PracticeType");
        var response = await client.GetStudentCurriculumAsync(request, headers, cancellationToken: cancellationToken);

        return response.Curriculum
            .Concat(response.Electives.SelectMany(group => group.Disciplines))
            .Where(item => item.DisciplineId > 0)
            .GroupBy(item => item.DisciplineId)
            .Select(group =>
            {
                var first = group.First();
                var semesters = group.SelectMany(item => item.Semesters).ToArray();
                var objectType = ResolveObjectType(first);
                var practiceType = ResolvePracticeType(first);
                return new Discipline(group.Key, first.Discipline,
                    semesters.Select(semester => semester.Semester).Distinct().OrderBy(semester => semester).ToArray(),
                    objectType.Id, objectType.Name, practiceType.Id, practiceType.Name,
                    semesters.Any(semester => semester.CourseWork),
                    semesters.Any(semester => semester.CourseProject))
                {
                    Grade = grades?.GetValueOrDefault(NormalizeDisciplineName(first.Discipline))
                };
            })
            .ToArray();
    }

    private async Task<IReadOnlyDictionary<string, DisciplineGrade>> LoadGradesAsync(StudentRequest request,
        Metadata headers, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetStudentGradesAsync(request, headers,
                cancellationToken: cancellationToken);
            return response.Students
                // Если запрос по student_id, не смешиваем оценки других записей
                // этого же человека.
                .Where(student => request.StudentId == 0 || student.StudentId == request.StudentId)
                .SelectMany(student => student.Grades)
                .Select(grade => (Name: NormalizeDisciplineName(grade.Discipline), Grade: grade,
                    Value: PickGradeValue(grade)))
                .Where(item => item.Name.Length > 0 && item.Value is not null)
                .GroupBy(item => item.Name)
                .ToDictionary(group => group.Key, group => group
                    .OrderByDescending(item => item.Grade.ExamDate?.ToDateTimeOffset() ?? DateTimeOffset.MinValue)
                    .ThenByDescending(item => item.Grade.Semester)
                    .Select(item => new DisciplineGrade(item.Value!.Value, item.Value.Text, item.Value.ShortText))
                    .First());
        }
        catch (RpcException exception) when (exception.StatusCode != StatusCode.Cancelled)
        {
            // Оценки — дополнительная информация: без них список дисциплин
            // остаётся полезным.
            logger.LogWarning(exception, "Failed to load Umu grades; disciplines will be returned without grades.");
            return new Dictionary<string, DisciplineGrade>();
        }
    }

    private static GradeValue? PickGradeValue(Grade grade) =>
        new[] { grade.Grade_, grade.ExamGrade, grade.Total }
            .FirstOrDefault(value =>
                value is not null && (value.Value > 0 || !string.IsNullOrWhiteSpace(value.Text)));

    private static string NormalizeDisciplineName(string? name)
    {
        var parts = (name ?? string.Empty).Split(' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', parts).ToLowerInvariant().Replace('ё', 'е');
    }

    private static StudentRequest BuildStudentRequest(string? studentEmail, int? studentId,
        bool includeProfileNames = false)
    {
        var request = new StudentRequest();
        if (!string.IsNullOrWhiteSpace(studentEmail)) request.Email = studentEmail;
        if (studentId is > 0) request.StudentId = studentId.Value;
        // Имена справочников для группировки. Если сервер их не поддержит,
        // останутся только идентификаторы.
        if (includeProfileNames)
        {
            request.Include.Add("Group");
            request.Include.Add("Specialty");
            request.Include.Add("Level");
        }

        return request;
    }

    private static (int Id, string Name) ResolveGroup(StudentRecord record) =>
        record.GroupOneofCase == StudentRecord.GroupOneofOneofCase.Group
            ? (record.Group.Id, record.Group.Name)
            : (record.GroupId, string.Empty);

    private static string ResolveSpecialtyName(StudentRecord record) =>
        record.SpecialtyOneofCase == StudentRecord.SpecialtyOneofOneofCase.Specialty
            ? record.Specialty.Name
            : string.Empty;

    private static string ResolveLevelName(StudentRecord record) =>
        record.LevelOneofCase == StudentRecord.LevelOneofOneofCase.Level
            ? record.Level.Name
            : string.Empty;

    private static (int Id, string Name) ResolveObjectType(CurriculumItem item) =>
        item.ObjectTypeOneofCase == CurriculumItem.ObjectTypeOneofOneofCase.ObjectType
            ? (item.ObjectType.Id, item.ObjectType.Name)
            : (item.ObjectTypeId, ObjectTypeNames.GetValueOrDefault(item.ObjectTypeId, string.Empty));

    private static (int Id, string Name) ResolvePracticeType(CurriculumItem item) =>
        item.PracticeTypeOneofCase == CurriculumItem.PracticeTypeOneofOneofCase.PracticeType
            ? (item.PracticeType.Id, item.PracticeType.Name)
            : (item.PracticeTypeId, PracticeTypeNames.GetValueOrDefault(item.PracticeTypeId, string.Empty));
}
