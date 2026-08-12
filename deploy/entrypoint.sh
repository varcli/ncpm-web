#!/bin/sh
set -eu
umask 077

nginx -t
nginx

exec dotnet /app/Ncpm.Web.dll "$@"
