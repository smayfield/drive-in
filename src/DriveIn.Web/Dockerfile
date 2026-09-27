# Build from the repo root:  docker build -f src/DriveIn.Web/Dockerfile .
# The SDK stage runs on the build machine's architecture and cross-publishes
# for the target (e.g. linux/arm64), so no emulation is needed.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
# Copy the full source BEFORE restoring: the SDK only adds the package that ships
# _framework/blazor.web.js when it can see Razor components at restore time.
# Restoring from the .csproj alone silently drops it (buttons do nothing).
COPY src/DriveIn.Web/ DriveIn.Web/
RUN dotnet restore DriveIn.Web/DriveIn.Web.csproj -a $TARGETARCH
RUN dotnet publish DriveIn.Web/DriveIn.Web.csproj -c Release -a $TARGETARCH --no-restore -o /app
# Fail the image build if the Blazor script is ever missing.
RUN test -f /app/wwwroot/_framework/blazor.web.js

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Data Protection keys live here (a volume in production). Creating the directory
# with the app user's ownership lets a new named volume inherit it.
RUN mkdir /keys && chown $APP_UID /keys
USER $APP_UID
# ASP.NET Core images listen on port 8080 by default.
EXPOSE 8080
ENTRYPOINT ["dotnet", "DriveIn.Web.dll"]
