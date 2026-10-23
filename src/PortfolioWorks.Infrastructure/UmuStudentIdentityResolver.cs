using Grpc.Core;
using Microsoft.Extensions.Options;
using PortfolioWorks.Application;
using Umu;

namespace PortfolioWorks.Infrastructure;

public sealed class UmuStudentIdentityResolver(StudentService.StudentServiceClient client, IOptions<UmuOptions> options)
    : IStudentIdentityResolver
{
    public async Task<string?> FindEmailAsync(int studentId, CancellationToken cancellationToken)
    {
        var authorization = options.Value.Authorization;
        if (string.IsNullOrWhiteSpace(authorization))
            throw new InvalidOperationException("Umu:Authorization is not configured.");

        var request = new StudentRequest { StudentId = studentId };
        var headers = new Metadata { { "authorization", authorization } };
        var response = await client.GetStudentProfileAsync(request, headers, cancellationToken: cancellationToken);

        // УМУ возвращает все записи человека, включая выпускников. Сначала берём
        // email записи, по которой пришёл запрос: у бакалавриата и магистратуры
        // он может отличаться. Если у неё email нет — первый валидный из остальных.
        var matching = response.Students.FirstOrDefault(student =>
            student.Id == studentId && StudentEmail.IsValid(student.Email));
        var email = matching?.Email ?? response.Students.Select(student => student.Email).FirstOrDefault(StudentEmail.IsValid);
        return email is null ? null : StudentEmail.Normalize(email);
    }
}
