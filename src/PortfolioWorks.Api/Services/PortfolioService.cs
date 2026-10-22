using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;
using PortfolioWorks.Application;

namespace PortfolioWorks.Api.Services;

public sealed class PortfolioService(IStudentCurriculumProvider curriculumProvider, IWorkSubmissionService workService,
    IOptions<UploadOptions> uploadOptions, ILogger<PortfolioService> logger)
    : global::PortfolioWorks.Api.PortfolioService.PortfolioServiceBase
{
    public override async Task<GetDisciplinesResponse> GetDisciplines(GetDisciplinesRequest request, ServerCallContext context)
    {
        var studentEmail = RequireStudentEmail(request.StudentEmail);
        var result = await curriculumProvider.GetCurriculumAsync(studentEmail, context.CancellationToken);
        var response = new GetDisciplinesResponse();
        response.Disciplines.AddRange(result.Select(x => new Discipline
        {
            Id = x.Id, Name = x.Name, Semesters = { x.Semesters }
        }));
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
        content.Position = 0;
        var result = await workService.UploadAsync(new(header.StudentEmail, header.DisciplineId, header.FileName,
            header.ContentType, content), context.CancellationToken);
        return new UploadWorkResponse { Id = result.Work?.Id.ToString() ?? string.Empty, Error = result.Error ?? string.Empty };
    }

    public override async Task<ListWorksResponse> ListWorks(ListWorksRequest request, ServerCallContext context)
    {
        var studentEmail = RequireStudentEmail(request.StudentEmail);
        var works = await workService.GetByStudentEmailAsync(studentEmail, context.CancellationToken);

        // Названия дисциплин приходят из УМУ, но список загруженных работ хранится
        // локально. Недоступность УМУ не должна скрывать уже загруженные файлы.
        PortfolioWorks.Application.Discipline[] curriculum = [];
        try
        {
            curriculum = (await curriculumProvider.GetCurriculumAsync(studentEmail, context.CancellationToken)).ToArray();
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
            {
                work.Discipline = new Discipline
                {
                    Id = discipline.Id,
                    Name = discipline.Name,
                    Semesters = { discipline.Semesters }
                };
            }
            return work;
        }));
        return response;
    }

    private static bool HasConflictingMetadata(UploadWorkRequest chunk, UploadWorkRequest header) =>
        (!string.IsNullOrEmpty(chunk.StudentEmail) && chunk.StudentEmail != header.StudentEmail) ||
        (chunk.DisciplineId != 0 && chunk.DisciplineId != header.DisciplineId) ||
        (!string.IsNullOrEmpty(chunk.FileName) && chunk.FileName != header.FileName) ||
        (!string.IsNullOrEmpty(chunk.ContentType) && chunk.ContentType != header.ContentType);

    private static string RequireStudentEmail(string studentEmail) =>
        StudentEmail.IsValid(studentEmail)
            ? StudentEmail.Normalize(studentEmail)
            : throw new RpcException(new Status(StatusCode.InvalidArgument, "A valid student_email is required."));
}
