# syntax=docker/dockerfile:1
#
# FileManager: ASP.NET Core 10 API + Angular 22 SPA in one image.
# The container is its own "host": it manages /etc/passwd, /etc/shadow, /etc/group,
# /etc/sudoers.d and POSIX ACLs inside the container, and browses the mounted volumes.
#
# The administrator account (requirement 5) is created by the entrypoint script below
# from FM_BOOTSTRAP_ADMIN_USER / FM_BOOTSTRAP_ADMIN_PASSWORD (see .env / docker-compose.yml).

# ---------------------------------------------------------------- stage 1: Angular
FROM node:24-bookworm-slim AS web

WORKDIR /src/web
# Only the files the production build needs are copied, so a developer's node_modules never
# leaks into the image and no .dockerignore is required.
COPY web/package.json web/package-lock.json ./
RUN npm ci

COPY web/angular.json web/tsconfig.json web/tsconfig.app.json ./
COPY web/public ./public
COPY web/src ./src
# Localized production build (ru + en) into ../src/FileManager.Api/wwwroot/<locale>
RUN npx ng build --configuration production

# ---------------------------------------------------------------- stage 2: API
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src
COPY global.json Directory.Build.props FileManager.slnx ./
COPY src/FileManager.Core/FileManager.Core.csproj src/FileManager.Core/
COPY src/FileManager.Api/FileManager.Api.csproj src/FileManager.Api/
RUN dotnet restore src/FileManager.Api/FileManager.Api.csproj

COPY src/ src/
# Drop any bin/obj that came from the build context so the container build is self-contained.
RUN find src -type d \( -name obj -o -name bin \) -prune -exec rm -rf {} +

COPY --from=web /src/src/FileManager.Api/wwwroot src/FileManager.Api/wwwroot
RUN test -f src/FileManager.Api/wwwroot/ru/index.html \
    && test -f src/FileManager.Api/wwwroot/en/index.html \
    && dotnet publish src/FileManager.Api/FileManager.Api.csproj \
        -c Release -o /app/publish /p:UseAppHost=false

# ---------------------------------------------------------------- stage 3: runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# acl  -> setfacl/getfacl for per-user directory access (requirement 4)
# passwd -> useradd/usermod/userdel/chpasswd (requirements 4 and 5)
# libpam-modules -> pam_unix.so for host password authentication
RUN apt-get update \
    && apt-get install -y --no-install-recommends acl passwd libpam-modules \
    && rm -rf /var/lib/apt/lists/*

# Dedicated PAM service: password authentication against /etc/shadow, no nullok.
# The carriage return check turns a CRLF checkout of this file into a build error instead of a
# PAM failure at runtime.
RUN printf '%s\n' \
        'auth      required   pam_unix.so' \
        'account   required   pam_unix.so' \
        'password  required   pam_unix.so' \
        'session   required   pam_unix.so' \
        > /etc/pam.d/filemanager \
    && ! grep -q "$(printf '\r')" /etc/pam.d/filemanager

# The admin group our application recognises (FileManager:AdminGroups).
RUN getent group sudo >/dev/null || groupadd --system sudo

WORKDIR /app
COPY --from=build /app/publish /app

# Entry point: create the emergency administrator when the password is provided and the
# account does not exist yet, then start the API (which serves the SPA from wwwroot).
RUN cat > /usr/local/bin/filemanager-entrypoint.sh <<'FM_SCRIPT'
#!/bin/sh
set -eu

admin_user="${FM_BOOTSTRAP_ADMIN_USER:-a-admin}"
admin_password="${FM_BOOTSTRAP_ADMIN_PASSWORD:-}"

if [ -n "$admin_password" ] && ! id -u "$admin_user" >/dev/null 2>&1; then
    echo "filemanager: creating administrator '$admin_user'"
    useradd -m -U -s /bin/bash -c "FileManager administrator" "$admin_user"
    printf '%s:%s\n' "$admin_user" "$admin_password" | chpasswd
    usermod -aG sudo "$admin_user"
    printf '# Managed by FileManager. Do not edit manually.\n%s ALL=(ALL:ALL) ALL\n' "$admin_user" > "/etc/sudoers.d/$admin_user"
    chmod 0440 "/etc/sudoers.d/$admin_user"
fi

exec dotnet /app/FileManager.Api.dll
FM_SCRIPT

# A Dockerfile checked out with Windows line endings (git core.autocrlf on Windows) puts a carriage
# return into every heredoc line, including the shebang: the kernel then tries to run "/bin/sh\r" and
# the container dies with
#   exec /usr/local/bin/filemanager-entrypoint.sh: no such file or directory
# Strip CRs and fail the build loudly when the script is not what it must be.
RUN tr -d '\r' < /usr/local/bin/filemanager-entrypoint.sh > /tmp/fm-entrypoint.sh \
    && install -m 0755 /tmp/fm-entrypoint.sh /usr/local/bin/filemanager-entrypoint.sh \
    && rm -f /tmp/fm-entrypoint.sh \
    && head -n 1 /usr/local/bin/filemanager-entrypoint.sh | grep -qx '#!/bin/sh' \
    && ! grep -q "$(printf '\r')" /usr/local/bin/filemanager-entrypoint.sh \
    && grep -q 'exec dotnet /app/FileManager.Api.dll' /usr/local/bin/filemanager-entrypoint.sh

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

# /bin/sh is passed explicitly, so even a stray carriage return in the shebang cannot stop the start.
ENTRYPOINT ["/bin/sh", "/usr/local/bin/filemanager-entrypoint.sh"]
