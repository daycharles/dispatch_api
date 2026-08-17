# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source

# Copy project files first so restore is cached independently of source edits.
COPY DispatchApi.sln ./
COPY src/DispatchApi/DispatchApi.csproj src/DispatchApi/
COPY tests/DispatchApi.Tests/DispatchApi.Tests.csproj tests/DispatchApi.Tests/
RUN dotnet restore

COPY . .
RUN dotnet test --no-restore --verbosity minimal
RUN dotnet publish src/DispatchApi/DispatchApi.csproj \
      --no-restore \
      -c Release \
      -o /app

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Run as the non-root user the base image already provides.
USER $APP_UID

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080

ENTRYPOINT ["dotnet", "DispatchApi.dll"]
