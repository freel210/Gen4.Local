#!/usr/bin/env bash
set -e

until awslocal s3 ls >/dev/null 2>&1; do
  sleep 2
done

awslocal s3 mb s3://lyra3 || true