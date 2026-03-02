FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["MeetingAssistant.sln", "./"]
COPY ["MeetingAssistant.Api/MeetingAssistant.Api.csproj", "MeetingAssistant.Api/"]
COPY ["MeetingAssistant.Core/MeetingAssistant.Core.csproj", "MeetingAssistant.Core/"]
COPY ["MeetingAssistant.Data/MeetingAssistant.Data.csproj", "MeetingAssistant.Data/"]
COPY ["MeetingAssistant.Infrastructure/MeetingAssistant.Infrastructure.csproj", "MeetingAssistant.Infrastructure/"]
COPY ["MeetingAssistant.Service/MeetingAssistant.Service.csproj", "MeetingAssistant.Service/"]

RUN dotnet restore "MeetingAssistant.Api/MeetingAssistant.Api.csproj"

COPY . .
WORKDIR "/src/MeetingAssistant.Api"
RUN dotnet publish "MeetingAssistant.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080
ENV ASPNETCORE_ENVIRONMENT=Development
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MeetingAssistant.Api.dll"]
