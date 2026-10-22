FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY PortfolioWorks.sln ./
COPY src/PortfolioWorks.Domain/PortfolioWorks.Domain.csproj src/PortfolioWorks.Domain/
COPY src/PortfolioWorks.Application/PortfolioWorks.Application.csproj src/PortfolioWorks.Application/
COPY src/PortfolioWorks.Infrastructure/PortfolioWorks.Infrastructure.csproj src/PortfolioWorks.Infrastructure/
COPY src/PortfolioWorks.Api/PortfolioWorks.Api.csproj src/PortfolioWorks.Api/
RUN dotnet restore PortfolioWorks.sln

COPY . ./
RUN dotnet publish src/PortfolioWorks.Api/PortfolioWorks.Api.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "PortfolioWorks.Api.dll"]
