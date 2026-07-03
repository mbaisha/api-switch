#!/usr/bin
docker compose build --no-cache

docker push docker.cnb.cool/vxlife/apiswitch/apiswitch-ui:latest
docker push docker.cnb.cool/vxlife/apiswitch/apiswitch-api:latest