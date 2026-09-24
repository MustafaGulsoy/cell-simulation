#!/usr/bin/env bash
# Deploys the game server to the VPS: runs the tests, publishes the app HERE, ships only the finished
# output, builds a small runtime image on the box, swaps the container, health-checks it, and rolls back
# to the previous image if it's unhealthy.
#
# Why publish locally: the production box is shared and heavily loaded, and a nightly `docker image prune -af`
# there deletes the 800 MB .NET SDK image, so building on the server meant re-downloading and compiling it
# under load (deploys took 30+ minutes or never finished). Shipping the published output only needs the
# small aspnet runtime image.
#
# Usage: Server/deploy.sh [user@host]        (default root@69.62.120.208, key-based ssh)
# Manual rollback: ssh HOST 'docker rm -f cellsimulator-server && docker run -d --name cellsimulator-server --restart unless-stopped -p 7778:7778/udp -p 127.0.0.1:5283:5000 -v cellsim-data:/data --cpu-shares 4096 cellsimulator-server:previous'
set -euo pipefail

HOST=${1:-root@69.62.120.208}
cd "$(dirname "$0")"

dotnet test -c Release --nologo -v q # never ship a red build

STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
dotnet publish CellSimulator.Server/CellSimulator.Server.csproj -c Release --nologo -v q -o "$STAGE/publish"
cp CellSimulator.Server/Dockerfile.prebuilt "$STAGE/Dockerfile"

# The remote script goes over ssh as the command argument (not stdin) because stdin carries the tarball.
read -r -d '' REMOTE <<'EOF' || true
set -euo pipefail
NAME=cellsimulator-server
RUN="docker run -d --name $NAME --restart unless-stopped -p 7778:7778/udp -p 127.0.0.1:5283:5000 -v cellsim-data:/data --cpu-shares 4096"

rm -rf /tmp/cellsim-build && mkdir /tmp/cellsim-build
tar -xzf - -C /tmp/cellsim-build

docker tag $NAME:latest $NAME:previous 2>/dev/null || true
docker build -q -t $NAME:latest /tmp/cellsim-build
rm -rf /tmp/cellsim-build

docker rm -f $NAME >/dev/null 2>&1 || true
$RUN $NAME:latest >/dev/null

for i in $(seq 1 30); do
  sleep 1
  if curl -fsS -m 2 http://127.0.0.1:5283/health >/dev/null; then echo "deployed: $NAME healthy"; exit 0; fi
done

echo "health check failed - rolling back" >&2
docker logs --tail 30 $NAME >&2 || true
docker rm -f $NAME >/dev/null
$RUN $NAME:previous >/dev/null
exit 1
EOF

tar -C "$STAGE" -czf - . | ssh "$HOST" "$REMOTE"
