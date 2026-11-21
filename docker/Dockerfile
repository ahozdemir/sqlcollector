# Builds the SqlDataCollector host (Web/Worker) and the sample "existing Hangfire host".
#   docker build -f docker/Dockerfile --target app .
#   docker build -f docker/Dockerfile --target existing-host .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json SqlCollector.sln ./
COPY src/ src/
COPY samples/ samples/
RUN dotnet publish src/SqlDataCollector/SqlDataCollector.csproj -c Release -o /out/app \
 && dotnet publish samples/ExistingHangfireHost/ExistingHangfireHost.csproj -c Release -o /out/existing-host

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime-base
# libgssapi is needed by the Negotiate (Kerberos) handler on Linux; harmless when unused
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
ENV DOTNET_RUNNING_IN_CONTAINER=true TZ=Europe/Istanbul

FROM runtime-base AS existing-host
WORKDIR /app
COPY --from=build /out/existing-host .
ENTRYPOINT ["dotnet", "ExistingHangfireHost.dll"]

FROM runtime-base AS app
WORKDIR /app
COPY --from=build /out/app .
# URLS (not only ASPNETCORE_URLS) because appsettings.json defines "Urls"
ENV URLS=http://+:8080 ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SqlDataCollector.dll"]
