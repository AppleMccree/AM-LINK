FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/ClassInterpreter.ClassroomServer/ClassInterpreter.ClassroomServer.csproj
RUN dotnet publish src/ClassInterpreter.ClassroomServer/ClassInterpreter.ClassroomServer.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /data
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "AMLink.ClassroomServer.dll"]
