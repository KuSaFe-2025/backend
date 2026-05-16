FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

COPY KuSaFeBackend.csproj ./
RUN dotnet restore KuSaFeBackend.csproj

COPY . ./
RUN dotnet publish KuSaFeBackend.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app

ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "KuSaFeBackend.dll"]
