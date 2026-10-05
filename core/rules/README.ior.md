# Bundled sing-box GeoIP rule set

`geoip-ru.srs` comes from the official SagerNet/sing-geoip `rule-set` branch:
https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/geoip-ru.srs

SHA-256: `6E23F5580DD443E2F9C4895ADAFEE8D199C1A3487182AD5F0A2256CF59C7E53A`.
The upstream license notice is included as `LICENSE-sing-geoip`.

The bundled file is loaded locally at startup, so the selected proxy does not
need to be working before this rule set can be read. Other GeoIP/Geosite tags
still use direct downloads and the sing-box cache.
