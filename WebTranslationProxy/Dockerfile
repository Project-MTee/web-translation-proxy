FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
ARG NUGET_FEED_URL
ARG NUGET_PAT

WORKDIR /src
COPY ["WebTranslationProxy/WebTranslationProxy.csproj", "WebTranslationProxy/"]
RUN dotnet restore "WebTranslationProxy/WebTranslationProxy.csproj"
COPY . .
WORKDIR "/src/WebTranslationProxy"
RUN dotnet build "WebTranslationProxy.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "WebTranslationProxy.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "WebTranslationProxy.dll"]