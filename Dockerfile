FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY AzureCardsMcpHttp.slnx .
COPY src/Backend/McpToolkit/McpToolkit.csproj src/Backend/McpToolkit/
COPY src/Backend/GeraApiKey/GeraApiKey.csproj src/Backend/GeraApiKey/
COPY src/Backend/AzureCardsMcpHttp/AzureCardsMcpHttp.csproj src/Backend/AzureCardsMcpHttp/
RUN dotnet restore src/Backend/AzureCardsMcpHttp/AzureCardsMcpHttp.csproj

COPY src/ src/
RUN dotnet publish src/Backend/AzureCardsMcpHttp/AzureCardsMcpHttp.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN useradd -m appuser \
    && chown -R appuser:appuser /app
USER appuser

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "AzureCardsMcpHttp.dll"]

## docker build -t filipefk/azure-cards-mcp:1.0 -t filipefk/azure-cards-mcp:latest .
## docker push filipefk/azure-cards-mcp:1.0
## docker push filipefk/azure-cards-mcp:latest
