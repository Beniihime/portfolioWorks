using Grpc.Core;
using Microsoft.Extensions.Options;
using PortfolioWorks.Application;
using Umu;

namespace PortfolioWorks.Infrastructure;

public sealed class UmuCurriculumProvider(StudentService.StudentServiceClient client, IOptions<UmuOptions> options)
    : IStudentCurriculumProvider
{
    public async Task<IReadOnlyCollection<Discipline>> GetCurriculumAsync(string studentEmail,
        CancellationToken cancellationToken)
    {
        var authorization = options.Value.Authorization;
        if (string.IsNullOrWhiteSpace(authorization))
            throw new InvalidOperationException("Umu:Authorization is not configured.");

        var request = new StudentRequest { Email = studentEmail };
        // include не запрашиваем: ObjectType/PracticeType в ответе пока не используются,
        // а включение include заполняет oneof объектом и обнуляет соответствующие *_id.
        var headers = new Metadata { { "authorization", authorization } };
        var response = await client.GetStudentCurriculumAsync(request, headers, cancellationToken: cancellationToken);

        return response.Curriculum
            .Concat(response.Electives.SelectMany(group => group.Disciplines))
            .Where(item => item.DisciplineId > 0)
            .GroupBy(item => item.DisciplineId)
            .Select(group => new Discipline(group.Key, group.First().Discipline,
                group.SelectMany(item => item.Semesters)
                    .Select(semester => semester.Semester)
                    .Distinct()
                    .OrderBy(semester => semester)
                    .ToArray()))
            .ToArray();
    }
}
