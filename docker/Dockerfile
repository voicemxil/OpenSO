# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy ONLY the .csproj files (preserving layout) and restore first, so the NuGet restore lands in its
# own layer keyed on the project files alone. Source edits — the overwhelming majority of commits — then
# reuse the cached restore layer (via the buildx type=gha cache) instead of re-downloading every package.
# The list is the full ProjectReference closure of FSO.Server.Core; a project added to that graph must be
# added here too or `dotnet restore` fails loudly (it can't silently drift).
COPY TSOClient/Directory.Build.props TSOClient/
COPY TSOClient/FSO.Server.Core/FSO.Server.Core.csproj TSOClient/FSO.Server.Core/
COPY TSOClient/FSO.Content.TSO/FSO.Content.TSO.csproj TSOClient/FSO.Content.TSO/
COPY TSOClient/FSO.Server.Api.Core/FSO.Server.Api.Core.csproj TSOClient/FSO.Server.Api.Core/
COPY TSOClient/FSO.Server/FSO.Server.csproj TSOClient/FSO.Server/
COPY TSOClient/FSO.Server.Common/FSO.Server.Common.csproj TSOClient/FSO.Server.Common/
COPY TSOClient/FSO.Server.Database/FSO.Server.Database.csproj TSOClient/FSO.Server.Database/
COPY TSOClient/FSO.Server.Domain/FSO.Server.Domain.csproj TSOClient/FSO.Server.Domain/
COPY TSOClient/FSO.Server.Protocol/FSO.Server.Protocol.csproj TSOClient/FSO.Server.Protocol/
COPY TSOClient/FSO.Server.Clients/FSO.Server.Clients.csproj TSOClient/FSO.Server.Clients/
COPY TSOClient/FSO.Common.DatabaseService/FSO.Common.DatabaseService.csproj TSOClient/FSO.Common.DatabaseService/
COPY TSOClient/FSO.Server.DataService/FSO.Common.DataService.csproj TSOClient/FSO.Server.DataService/
COPY TSOClient/FSO.Common.Domain/FSO.Common.Domain.csproj TSOClient/FSO.Common.Domain/
COPY TSOClient/tso.common/FSO.Common.csproj TSOClient/tso.common/
COPY TSOClient/tso.content/FSO.Content.csproj TSOClient/tso.content/
COPY TSOClient/tso.files/FSO.Files.csproj TSOClient/tso.files/
COPY TSOClient/tso.simantics/FSO.SimAntics.csproj TSOClient/tso.simantics/
COPY TSOClient/tso.sound/FSO.HIT.csproj TSOClient/tso.sound/
COPY TSOClient/tso.world/FSO.LotView.csproj TSOClient/tso.world/
COPY TSOClient/tso.vitaboy.engine/FSO.Vitaboy.Engine.csproj TSOClient/tso.vitaboy.engine/
COPY TSOClient/tso.vitaboy.model/FSO.Vitaboy.csproj TSOClient/tso.vitaboy.model/
COPY Other/libs/MSDFData/MSDFData.csproj Other/libs/MSDFData/
COPY Other/libs/TargaImagePCL/TargaImagePCL.csproj Other/libs/TargaImagePCL/
RUN dotnet restore TSOClient/FSO.Server.Core/FSO.Server.Core.csproj

# Now bring in the full source (bin/obj/.git are kept out by the repo-root .dockerignore, so stale local
# build output can't bust these layers or leak into the image) and build against the cached restore.
COPY TSOClient/ TSOClient/
COPY Other/libs/MSDFData/ Other/libs/MSDFData/
COPY Other/libs/TargaImagePCL/ Other/libs/TargaImagePCL/
RUN dotnet publish TSOClient/FSO.Server.Core/FSO.Server.Core.csproj \
    -c Release --self-contained false --no-restore -o /app

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0

# Release version stamped into the image so the server self-reports it (ServerVersion.Get reads
# version.txt). CI passes the release tag (e.g. dev-1) on a release build, or "edge" for main builds;
# empty leaves the source placeholder (dev-0) for local `docker build`.
ARG VERSION=""

LABEL org.opencontainers.image.title="OpenSO Server"
LABEL org.opencontainers.image.source="https://github.com/voicemxil/OpenSO"
LABEL org.opencontainers.image.licenses="MPL-2.0"

WORKDIR /app
COPY --from=build /app .
RUN if [ -n "$VERSION" ]; then printf '%s\n' "$VERSION" > /app/version.txt; fi
COPY docker/entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh
EXPOSE 9000 33100 34100 35100
ENTRYPOINT ["/app/entrypoint.sh"]
