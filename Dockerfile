FROM mcr.microsoft.com/dotnet/aspnet:5.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:5.0 AS build
ARG NUGET_FEED_URL
ARG NUGET_PAT

WORKDIR /src
COPY ["WebTranslationProxy.csproj", ""]
RUN dotnet nuget add source $NUGET_FEED_URL -u "whatever" -p $NUGET_PAT --store-password-in-clear-text --valid-authentication-types "basic"
RUN dotnet restore "./WebTranslationProxy.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "WebTranslationProxy.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "WebTranslationProxy.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "WebTranslationProxy.dll"]