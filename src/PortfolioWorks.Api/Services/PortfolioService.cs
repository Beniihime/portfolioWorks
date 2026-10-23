using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;
using PortfolioWorks.Application;

namespace PortfolioWorks.Api.Services;

public sealed class PortfolioService(IStudentCurriculumProvider curriculumProvider, IWorkSubmissionService workService,
    IWorkDownloadService downloadService, IStudentIdentityResolver identityResolver,
    IOptions<UploadOptions> uploadOptions, ILogger<PortfolioService> logger)
    : global::PortfolioWorks.Api.PortfolioService.PortfolioServiceBase
{
    public override async Task<GetDisciplinesResponse> GetDisciplines(GetDisciplinesRequest request, ServerCallContext context)
    {
        var (studentEmail, studentId) = RequireStudentIdentity(request.StudentEmail, request.StudentId);
        var groups = await curriculumProvider.GetCurriculumAsync(studentEmail, studentId, includeGrades: true,
            context.CancellationToken);
        var works = await GetWorksAsync(studentEmail, studentId, context.CancellationToken);
        var worksByDiscipline = works
            .GroupBy(work => work.DisciplineId)
            .ToDictionary(group => group.Key, group =>
                (IReadOnlyCollection<PortfolioWorks.Domain.UploadedWork>)group.ToArray());

        var response = new GetDisciplinesResponse();
        response.Groups.AddRange(groups
            .Select(group => ToProto(group, worksByDiscipline))
            .Where(group => group.Disciplines.Count > 0));
        return response;
    }

    public override async Task<UploadWorkResponse> UploadWork(IAsyncStreamReader<UploadWorkRequest> stream, ServerCallContext context)
    {
        var maximumFileSize = uploadOptions.Value.MaximumFileSizeBytes;
        UploadWorkRequest? header = null;
        await using var content = new MemoryStream();
        while (await stream.MoveNext(context.CancellationToken))
        {
            var item = stream.Current;
            if (header is null)
                header = item;
            else if (HasConflictingMetadata(item, header))
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    "File metadata must be omitted or identical in every chunk after the first one."));
            item.Content.WriteTo(content);
            if (content.Length > maximumFileSize)
                return new UploadWorkResponse { Error = "File exceeds the configured size limit." };
        }
        if (header is null) throw new RpcException(new Status(StatusCode.InvalidArgument, "Upload is empty."));
        var studentEmail = await ResolveStudentEmailAsync(header.StudentEmail, header.StudentId,
            context.CancellationToken);
        content.Position = 0;
        var result = await workService.UploadAsync(new(studentEmail, header.DisciplineId, header.FileName,
            header.ContentType, content), context.CancellationToken);
        return new UploadWorkResponse { Id = result.Work?.Id.ToString() ?? string.Empty, Error = result.Error ?? string.Empty };
    }

    public override async Task<ListWorksResponse> ListWorks(ListWorksRequest request, ServerCallContext context)
    {
        var studentEmail = await ResolveStudentEmailAsync(request.StudentEmail, request.StudentId,
            context.CancellationToken);
        var works = await workService.GetByStudentEmailAsync(studentEmail, context.CancellationToken);

        // Названия дисциплин приходят из УМУ, но список загруженных работ хранится
        // локально. Недоступность УМУ не должна скрывать уже загруженные файлы.
        PortfolioWorks.Application.Discipline[] curriculum = [];
        try
        {
            curriculum = (await curriculumProvider.GetCurriculumAsync(studentEmail, null, includeGrades: false,
                    context.CancellationToken))
                .SelectMany(group => group.Disciplines)
                .ToArray();
        }
        catch (Exception exception) when (exception is RpcException or HttpRequestException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Failed to load curriculum from Umu for {StudentEmail}", studentEmail);
        }

        var disciplines = curriculum.ToDictionary(x => x.Id);

        var response = new ListWorksResponse();
        response.Works.AddRange(works.Select(x =>
        {
            var work = new UploadedWork
            {
                Id = x.Id.ToString(),
                FileName = x.OriginalFileName,
                ContentType = x.ContentType,
                SizeBytes = x.SizeBytes,
                UploadedAt = Timestamp.FromDateTimeOffset(x.UploadedAt)
            };
            if (disciplines.TryGetValue(x.DisciplineId, out var discipline))
                work.Discipline = ToProto(discipline);
            return work;
        }));
        return response;
    }

    public override async Task DownloadWork(DownloadWorkRequest request,
        IServerStreamWriter<DownloadWorkResponse> responseStream, ServerCallContext context)
    {
        if (!Guid.TryParse(request.WorkId, out var workId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "work_id must be a valid GUID."));

        var studentEmail = await ResolveStudentEmailAsync(request.StudentEmail, request.StudentId,
            context.CancellationToken);
        var work = await downloadService.FindAsync(workId, studentEmail, context.CancellationToken);
        if (work is null)
            throw new RpcException(new Status(StatusCode.NotFound, "Work is not found."));

        const int chunkSize = 64 * 1024;
        var buffer = new byte[chunkSize];
        var headerSent = false;
        try
        {
            await downloadService.ReadAsync(work, async (source, cancellationToken) =>
            {
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    var message = new DownloadWorkResponse();
                    if (!headerSent)
                    {
                        message.FileName = work.OriginalFileName;
                        message.ContentType = work.ContentType;
                        message.SizeBytes = work.SizeBytes;
                        headerSent = true;
                    }

                    message.Content = ByteString.CopyFrom(buffer, 0, read);
                    await responseStream.WriteAsync(message, cancellationToken);
                }
            }, context.CancellationToken);
        }
        catch (WorkFileNotFoundException exception)
        {
            logger.LogWarning(exception, "Stored file for work {WorkId} is missing", workId);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Stored file is not available."));
        }

        // Пустой файл загрузка не пропускает, но если такой всё же оказался в БД,
        // клиент должен получить хотя бы метаданные.
        if (!headerSent)
            await responseStream.WriteAsync(new DownloadWorkResponse
            {
                FileName = work.OriginalFileName,
                ContentType = work.ContentType,
                SizeBytes = work.SizeBytes
            }, context.CancellationToken);
    }

    private static StudyGroup ToProto(PortfolioWorks.Application.StudyGroup group,
        IReadOnlyDictionary<int, IReadOnlyCollection<PortfolioWorks.Domain.UploadedWork>> works) => new()
    {
        StudentId = group.StudentId,
        GroupId = group.GroupId,
        GroupName = group.GroupName,
        SpecialtyName = group.SpecialtyName,
        LevelName = group.LevelName,
        Disciplines = { group.Disciplines.Where(x => x.IsCourseWorkOrPractice).Select(d => ToProto(d, works)) }
    };

    private static Discipline ToProto(PortfolioWorks.Application.Discipline discipline,
        IReadOnlyDictionary<int, IReadOnlyCollection<PortfolioWorks.Domain.UploadedWork>> works)
    {
        var result = ToProto(discipline);
        result.Files.AddRange(works.GetValueOrDefault(discipline.Id, []).Select(ToProtoFile));
        return result;
    }

    private static WorkFile ToProtoFile(PortfolioWorks.Domain.UploadedWork work) => new()
    {
        Id = work.Id.ToString(),
        FileName = work.OriginalFileName,
        ContentType = work.ContentType,
        SizeBytes = work.SizeBytes,
        UploadedAt = Timestamp.FromDateTimeOffset(work.UploadedAt)
    };

    private static Discipline ToProto(PortfolioWorks.Application.Discipline discipline) => new()
    {
        Id = discipline.Id,
        Name = discipline.Name,
        Semesters = { discipline.Semesters },
        ObjectTypeId = discipline.ObjectTypeId,
        ObjectTypeName = discipline.ObjectTypeName,
        PracticeTypeId = discipline.PracticeTypeId,
        PracticeTypeName = discipline.PracticeTypeName,
        HasCourseWork = discipline.HasCourseWork,
        HasCourseProject = discipline.HasCourseProject,
        Grade = discipline.Grade is { } grade
            ? new DisciplineGrade { Value = grade.Value, Text = grade.Text, ShortText = grade.ShortText }
            : null
    };

    private static bool HasConflictingMetadata(UploadWorkRequest chunk, UploadWorkRequest header) =>
        (!string.IsNullOrEmpty(chunk.StudentEmail) && chunk.StudentEmail != header.StudentEmail) ||
        (chunk.DisciplineId != 0 && chunk.DisciplineId != header.DisciplineId) ||
        (!string.IsNullOrEmpty(chunk.FileName) && chunk.FileName != header.FileName) ||
        (!string.IsNullOrEmpty(chunk.ContentType) && chunk.ContentType != header.ContentType) ||
        (chunk.StudentId != 0 && chunk.StudentId != header.StudentId);

    private static (string? Email, int? StudentId) RequireStudentIdentity(string studentEmail, int studentId)
    {
        var email = StudentEmail.IsValid(studentEmail) ? StudentEmail.Normalize(studentEmail) : null;
        var id = studentId > 0 ? studentId : (int?)null;
        return (email, id) is (null, null)
            ? throw new RpcException(new Status(StatusCode.InvalidArgument,
                "A valid student_email or student_id is required."))
            : (email, id);
    }

    private async Task<string> ResolveStudentEmailAsync(string studentEmail, int studentId,
        CancellationToken cancellationToken)
    {
        if (StudentEmail.IsValid(studentEmail)) return StudentEmail.Normalize(studentEmail);
        if (studentId <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "A valid student_email or student_id is required."));

        var resolved = await identityResolver.FindEmailAsync(studentId, cancellationToken);
        return resolved ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
            "Cannot resolve student_email for the provided student_id."));
    }

    private async Task<IReadOnlyCollection<PortfolioWorks.Domain.UploadedWork>> GetWorksAsync(
        string? studentEmail, int? studentId, CancellationToken cancellationToken)
    {
        try
        {
            var email = await ResolveStudentEmailAsync(studentEmail ?? string.Empty, studentId ?? 0,
                cancellationToken);
            return await workService.GetByStudentEmailAsync(email, cancellationToken);
        }
        catch (RpcException exception) when (exception.StatusCode != StatusCode.Cancelled)
        {
            // Файлы — дополнение к дисциплинам: если email не резолвится,
            // список дисциплин всё равно нужно вернуть.
            logger.LogWarning(exception, "Failed to resolve student email; files will not be attached.");
            return [];
        }
    }
}
