# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["MeetingAssistant.sln", "./"]
COPY ["MeetingAssistant.Api/MeetingAssistant.Api.csproj", "MeetingAssistant.Api/"]

RUN dotnet restore

COPY . .

RUN dotnet publish "MeetingAssistant.Api/MeetingAssistant.Api.csproj" -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080
ENV ASPNETCORE_ENVIRONMENT=Development

EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "MeetingAssistant.Api.dll"]