# Caddy mit DNS-Anbietern für Let's Encrypt per DNS-Prüfung (DNS-01): Zertifikate auch für Server, die aus dem
# Internet nicht erreichbar sind oder andere Ports als 80/443 nutzen. Ohne feste Version wie beim Updater, damit
# Neubauten (rebuild.yml) die aktuellen Sicherheitsfixes bekommen.
FROM caddy:2-builder AS build
RUN xcaddy build \
    --with github.com/caddy-dns/cloudflare \
    --with github.com/caddy-dns/hetzner/v2 \
    --with github.com/caddy-dns/ionos \
    --with github.com/caddy-dns/netcup \
    --with github.com/caddy-dns/azure \
    --with github.com/caddy-dns/route53 \
    --with github.com/caddy-dns/digitalocean

FROM caddy:2-alpine
RUN apk upgrade --no-cache
COPY --from=build /usr/bin/caddy /usr/bin/caddy
