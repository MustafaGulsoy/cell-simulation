#!/usr/bin/env bash
# Deploys the game server to the VPS: runs the tests, ships the source, builds the image on the
# box, swaps the container, health-checks it, and rolls back to the previous image if it's unhealthy.
# Usage: Server/deploy.sh [user@host]        (default root@69.62.120.208, key-based ssh)
# Manual rollback: ssh HOST 'docker rm -f cellsimulator-server && docker run -d --name cellsimulator-server --restart unless-stopped -p 7778:7778/udp -p 127.0.0.1:5283:5000 cellsimulator-server:previous'
set -euo pipefail

HOST=${1:-root@69.62.120.208}
cd "$(dirname "$0")"

dotnet test -c Release --nologo -v q # never ship a red build

# The remote script goes over ssh as the command argument (not stdin) because stdin carries the tarball.
read -r -d '' REMOTE <<'EOF' || true
set -euo pipefail
NAME=cellsimulator-server
RUN="docker run -d --name $NAME --restart unless-stopped -p 7778:7778/udp -p 127.0.0.1:5283:5000"

rm -rf /tmp/cellsim-build && mkdir /tmp/cellsim-build
tar -xzf - -C /tmp/cellsim-build

docker tag $NAME:latest $NAME:previous 2>/dev/null || true
docker build -q -t $NAME:latest /tmp/cellsim-build/CellSimulator.Server
rm -rf /tmp/cellsim-build

docker rm -f $NAME >/dev/null 2>&1 || true
$RUN $NAME:latest >/dev/null

for i in 1 2 3 4 5 6 7 8 9 10; do
  sleep 1
  if curl -fsS -m 2 http://127.0.0.1:5283/health >/dev/null; then echo "deployed: $NAME healthy"; exit 0; fi
done

echo "health check failed - rolling back" >&2
docker logs --tail 30 $NAME >&2 || true
docker rm -f $NAME >/dev/null
$RUN $NAME:previous >/dev/null
exit 1
EOF

tar --exclude=bin --exclude=obj -czf - CellSimulator.Server | ssh "$HOST" "$REMOTE"
