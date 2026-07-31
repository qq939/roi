# jiangjimjim 核心包

这是「核心包」目录，每个核心包对应一个 S3 桶（这里桶名是 `jiangjimjim`），
里面只放这个桶的 S3 客户端和凭证。Web App 通过 `server.js` 调
`s3cli.py` 来操作桶；你也可以直接在终端把它当 CLI 用。

```
src/jiangjimjim/
├── .env         # AWS 凭证 + 桶/区域/默认 prefix（不要提交）
├── s3cli.py     # 唯一对外入口：Python CLI，被 server.js spawn 调用
└── README.md    # 本文件
```

> 核心包是「备用替换包」：Web UI 的「核心模块」面板会把它打成压缩包
> 上传到 `.corepkgs/`，需要时一键 `select` 替换回 `src/jiangjimjim/`。
> 所以「核心模块」= 当前 src 目录内容；「备用核心包」= .corepkgs 里的存档。
> 详见上层的 `SKILL.md` §4。

---

## 1. 安装

需要 Python 3.10+，并安装两个依赖：

```bash
pip install boto3 python-dotenv
```

容器内若没有，可让 `user_start.sh` 自动装好（见顶层 `user_start.sh`）。

## 2. 准备凭证

编辑本目录的 `.env`：

```bash
BUCKET_NAME=jiangjimjim
REGION=us-east-2
AWS_ACCESS_KEY_ID=AKIA...
AWS_SECRET_ACCESS_KEY=...
DEFAULT_PREFIX=photos/2024/        # 可选；启动时自动 ensure 存在
```

> 凭证**只**写在这里，不要贴到对话或 git。`s3cli.py` 通过
> `python-dotenv` 加载，从环境变量读 AWS 凭证。

## 3. 命令行使用

> 全部命令从 `src/jiangjimjim/` 目录运行，或在任意目录运行时显式
> 用 `cd` 切到 `src/jiangjimjim/` 后再跑（脚本里会 `load_dotenv()`）。

### 3.1 列文件

```bash
python3 s3cli.py --list                    # 列桶根
python3 s3cli.py --list --prefix photos/   # 列 photos/ 前缀
```

### 3.2 上传文件

```bash
python3 s3cli.py --upload ./local.txt                    # key = local.txt
python3 s3cli.py --upload ./local.txt --key photos/x.txt # 显式指定 key
```

### 3.3 下载文件

```bash
python3 s3cli.py --download photos/2024/1.txt              # 下载到 ./photos/2024/1.txt
python3 s3cli.py --download photos/2024/1.txt --out /tmp/x  # 下载到指定路径
```

### 3.4 删除

```bash
python3 s3cli.py --delete photos/2024/1.txt
```

### 3.5 新建目录

S3 没有真正的目录，新建目录等于放一个 0 字节、key 以 `/` 结尾的 marker：

```bash
python3 s3cli.py --mkdir photos/2025
```

### 3.6 自动确保 prefix 存在

启动时常用：递归确保每一级都建好。

```bash
python3 s3cli.py --ensure-prefix photos/2025/sub/
```

### 3.7 JSON 输出（供 server.js / 脚本解析）

任意命令加 `--json`，结果会以单行 JSON 写到 stdout：

```bash
python3 s3cli.py --list --prefix photos/2024/ --json
python3 s3cli.py --upload ./x.txt --key photos/x.txt --json
python3 s3cli.py --ensure-prefix photos/2024/ --json
```

成功形如 `{"ok": true, "action": "...", ...}`，失败形如
`{"ok": false, "error": "..."}` 并以 exit 1 退出。

## 4. 完整命令表

| 命令 | 必须参数 | 可选参数 | 说明 |
|---|---|---|---|
| `--upload`     | `LOCAL`        | `--key`  | 上传一个文件到桶 |
| `--download`   | `KEY`          | `--out`  | 从桶下载一个文件到本地 |
| `--list`       | —              | `--prefix` | 列出对象/目录 |
| `--delete`     | `KEY`          | —        | 删除一个对象 |
| `--mkdir`      | `PREFIX`       | —        | 创建 0 字节目录 marker |
| `--ensure-prefix` | `PREFIX`    | —        | 递归确保 prefix 每一级都建好 |
| 任意           | —              | `--json` | JSON 单行输出（用于机器解析） |

## 5. server.js 怎么用 s3cli.py

`server.js` 通过 `child_process.spawn('python3', [S3CLI_PY, ...])` 调
`--list / --upload / --download / --delete / --mkdir / --ensure-prefix`
来驱动真实 S3 操作（`/api/s3/*` 路由族）。它**不会**直接 `import`
boto3 —— `s3cli.py` 是 single source of truth，CLI 和 Web 行为完全一致。

> 压缩包解压（核心模块替换）走的是 `src/s3extract.py`（顶层，
> 独立 stdlib，不依赖 boto3 / .env），与本文件无关。

## 6. 复制到新桶

要把这套核心包复制给另一个 S3 桶，只改 3 处：

```bash
# 1. 复制目录
cp -r src/jiangjimjim src/your-bucket

# 2. 改名（仅 .env 里的桶名）
sed -i 's/jiangjimjim/your-bucket/g' src/your-bucket/.env

# 3. 填新凭证
$EDITOR src/your-bucket/.env
```

`server.js` 自动发现 `src/` 下任何目录作为核心包（详见顶层 `SKILL.md` §3.3）。

## 7. 常见错误

| 现象 | 原因 | 解决 |
|---|---|---|
| `NoCredentialsError` | `.env` 没填 AK/SK | 编辑本目录的 `.env` |
| `ModuleNotFoundError: dotenv` | Python 缺依赖 | `pip install boto3 python-dotenv`（或让 `user_start.sh` 自动装） |
| `AccessDenied` | IAM 策略不允许 | 收紧策略为只允许 `<bucket>` 的 `PutObject / GetObject / DeleteObject / ListBucket` |
| `--list` 空 | prefix 错 | 不带 `--prefix` 列根；带了就只列那个前缀 |

## 8. 文件清单

- `.env` — 凭证 / 桶 / 区域（**不**提交）
- `s3cli.py` — 唯一 CLI 入口
- `README.md` — 本文件
