# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore first so dependency layers are cached between builds
COPY WebApplication1.csproj ./
RUN dotnet restore WebApplication1.csproj

COPY . .
RUN dotnet publish WebApplication1.csproj -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Listen on HTTP inside the container; the host port is mapped at run time
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "WebApplication1.dll"]
