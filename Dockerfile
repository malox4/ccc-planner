FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Planner/Planner.csproj src/Planner/
RUN dotnet restore src/Planner/Planner.csproj
COPY src/Planner/ src/Planner/
RUN dotnet publish src/Planner/Planner.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
LABEL org.opencontainers.image.source="https://github.com/malox4/ccc-planner" \n      org.opencontainers.image.description="CCC Planner: sprint planning on top of Jira"
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Planner.dll"]
