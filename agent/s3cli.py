#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
s3cli.py — 亚马逊 S3 命令行客户端（jiangjimjim 核心包）
被 server.js 通过 child_process 调用，也是独立 CLI 工具。

只负责 S3 CRUD：上传 / 下载 / 列表 / 删除 / 建目录。
压缩包解压（用于核心模块替换）由 server.js 内的 src/s3extract.py 负责，
本文件不涉及。
"""

from dotenv import load_dotenv
load_dotenv()
import os, sys, argparse, mimetypes
import boto3

BUCKET_NAME    = os.getenv("BUCKET_NAME", "jiangjimjim")
REGION         = os.getenv("REGION", os.getenv("AWS_DEFAULT_REGION", "us-east-2"))
DEFAULT_PREFIX = os.getenv("DEFAULT_PREFIX", "")  # 启动时自动确保存在


def get_s3():
    return boto3.client("s3", region_name=REGION)


def ensure_prefix(prefix):
    """检查 S3 上 prefix 目录是否存在；不存在则递归创建每一级。"""
    if not prefix:
        return []
    prefix = prefix if prefix.endswith("/") else prefix + "/"
    s3 = get_s3()
    created = []
    parts = [p for p in prefix.split("/") if p]
    acc = ""
    for p in parts:
        acc += p + "/"
        try:
            resp = s3.list_objects_v2(Bucket=BUCKET_NAME, Prefix=acc, MaxKeys=1)
            if "Contents" not in resp and "CommonPrefixes" not in resp:
                s3.put_object(Bucket=BUCKET_NAME, Key=acc, Body=b"")
                created.append(acc)
        except Exception as e:
            print(f"  ! ensure_prefix({acc}) failed: {e}", file=sys.stderr)
    return created


def upload(local_path, key=None):
    if not os.path.isfile(local_path):
        print(f"✗ local file not found: {local_path}", file=sys.stderr)
        sys.exit(2)
    key = key or os.path.basename(local_path)
    s3 = get_s3()
    ct, _ = mimetypes.guess_type(local_path)
    extra = {"ContentType": ct} if ct else {}
    s3.upload_file(local_path, BUCKET_NAME, key, ExtraArgs=extra)
    print(f"OK uploaded {local_path} -> s3://{BUCKET_NAME}/{key}")


def download(key, local_path=None):
    local_path = local_path or key
    os.makedirs(os.path.dirname(os.path.abspath(local_path)) or ".", exist_ok=True)
    s3 = get_s3()
    s3.download_file(BUCKET_NAME, key, local_path)
    print(f"OK downloaded s3://{BUCKET_NAME}/{key} -> {local_path}")


def list_files(prefix=""):
    s3 = get_s3()
    paginator = s3.get_paginator("list_objects_v2")
    found = False
    for page in paginator.paginate(Bucket=BUCKET_NAME, Prefix=prefix, Delimiter="/"):
        for p in page.get("CommonPrefixes", []):
            print(f"  DIR  {p['Prefix']}")
            found = True
        for obj in page.get("Contents", []):
            # 0 字节且 key 以 / 结尾 → 目录 marker
            if obj["Size"] == 0 and obj["Key"].endswith("/"):
                print(f"  DIR  {obj['Key']}")
            else:
                print(f"  FILE {obj['Key']}\t{obj['Size']}B")
            found = True
    if not found:
        print("  (empty)")


def delete(key):
    s3 = get_s3()
    s3.delete_object(Bucket=BUCKET_NAME, Key=key)
    print(f"OK deleted s3://{BUCKET_NAME}/{key}")


def mkdir(prefix):
    if not prefix.endswith("/"):
        prefix += "/"
    s3 = get_s3()
    s3.put_object(Bucket=BUCKET_NAME, Key=prefix, Body=b"")
    print(f"OK created s3://{BUCKET_NAME}/{prefix}")


def main():
    p = argparse.ArgumentParser(description="jiangjimjim S3 CLI")
    p.add_argument("--upload", metavar="LOCAL", help="上传本地文件")
    p.add_argument("--download", metavar="KEY", help="下载桶内文件")
    p.add_argument("--list", action="store_true", help="列出桶内文件")
    p.add_argument("--delete", metavar="KEY", help="删除桶内对象")
    p.add_argument("--mkdir", metavar="PREFIX", help="创建目录（0 字节 marker）")
    p.add_argument("--ensure-prefix", metavar="PREFIX", help="确保 prefix 存在（不存在则创建）")
    p.add_argument("--prefix", metavar="PREFIX", help="--list 时只列某前缀")
    p.add_argument("--key", metavar="S3KEY", help="配合 --upload 指定目标 key")
    p.add_argument("--out", metavar="LOCAL", help="配合 --download 指定本地路径")
    p.add_argument("--json", action="store_true", help="以 JSON 行输出结果（供 server.js 解析）")
    args = p.parse_args()

    import json
    def emit(kind, **kw):
        if args.json:
            print(json.dumps({"ok": True, "action": kind, **kw}, ensure_ascii=False))

    try:
        if args.upload:
            upload(args.upload, key=args.key)
            emit("upload", key=args.key or os.path.basename(args.upload))
        elif args.download:
            download(args.download, local_path=args.out)
            emit("download", key=args.download)
        elif args.delete:
            delete(args.delete)
            emit("delete", key=args.delete)
        elif args.mkdir:
            mkdir(args.mkdir)
            emit("mkdir", prefix=args.mkdir)
        elif args.ensure_prefix:
            created = ensure_prefix(args.ensure_prefix)
            emit("ensure_prefix", prefix=args.ensure_prefix, created=created)
        elif args.list:
            list_files(prefix=args.prefix or "")
        else:
            p.print_help()
    except SystemExit:
        raise
    except Exception as e:
        if args.json:
            print(json.dumps({"ok": False, "error": str(e)}))
        else:
            print(f"✗ {e}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
