@echo off
docker buildx build --no-cache --tag sungaila/newdarkglobalserver:1.3.3 --tag sungaila/newdarkglobalserver:latest --platform linux/amd64,linux/arm64 --push -f "src/Dockerfile" .