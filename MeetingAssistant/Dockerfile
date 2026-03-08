FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["MeetingAssistant.sln", "./"]
COPY ["MeetingAssistant/MeetingAssistant.csproj", "MeetingAssistant/"]

RUN dotnet restore "MeetingAssistant/MeetingAssistant.csproj"

COPY . .
WORKDIR "/src/MeetingAssistant"
RUN dotnet publish "MeetingAssistant.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080
ENV ASPNETCORE_ENVIRONMENT=Development
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MeetingAssistant.dll"]
