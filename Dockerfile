FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/YunxiTiku.Web/YunxiTiku.Web.csproj src/YunxiTiku.Web/
RUN dotnet restore src/YunxiTiku.Web/YunxiTiku.Web.csproj
COPY src/YunxiTiku.Web/ src/YunxiTiku.Web/
COPY public/ src/YunxiTiku.Web/public/
RUN dotnet publish src/YunxiTiku.Web/YunxiTiku.Web.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8787
ENV App__SqlitePath=/app/data/question-bank.db
ENV App__DataAssetsRoot=/app/data/assets
ENV App__UserDataRoot=/app/userdata
COPY --from=build /app/publish .
VOLUME ["/app/data", "/app/userdata"]
EXPOSE 8787
ENTRYPOINT ["dotnet", "YunxiTiku.Web.dll"]
