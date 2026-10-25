FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/AutomateJob.Web/ src/AutomateJob.Web/
RUN dotnet publish src/AutomateJob.Web/AutomateJob.Web.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-venv python3-pip ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .
COPY agent/ agent/

RUN python3 -m venv /opt/agent-venv \
    && /opt/agent-venv/bin/pip install --no-cache-dir -r /app/agent/requirements.txt

ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN chmod +x /app/.playwright/node/linux-x64/node \
    && /app/.playwright/node/linux-x64/node /app/.playwright/package/cli.js install chromium --with-deps \
    && chrome="$(find /ms-playwright -type f -name chrome | head -n 1)" \
    && ln -sf "$chrome" /usr/local/bin/headless-chromium

ENV ASPNETCORE_URLS=http://+:8080 \
    DATA_DIR=/data \
    HEADLESS=true \
    PYTHON=/opt/agent-venv/bin/python \
    CHROME_PATH=/usr/local/bin/headless-chromium \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright

EXPOSE 8080
VOLUME /data
ENTRYPOINT ["dotnet", "AutomateJob.Web.dll"]
