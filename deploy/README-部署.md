# AM-LINK 云端课堂部署

这套部署用于第一阶段真实试用：一台新加坡 Linux 服务器、一个 HTTPS 域名、多个教师浏览器和现有 Windows 学生客户端。服务器只保存匿名问题、点赞、困惑信号和老师广播，不保存学生姓名、录音、完整字幕或课件。

## 准备

1. 准备一台带公网 IP 的 Ubuntu 服务器，开放 TCP 80 和 443。
2. 把域名的 A 记录指向服务器公网 IP，例如 `classroom.example.com`。
3. 安装 Docker Engine 和 Docker Compose 插件。
4. 把整个源码目录复制到服务器。

## 启动

在 `deploy` 目录执行：

```bash
cp .env.example .env
nano .env
docker compose up -d --build
docker compose logs -f
```

必须把 `.env` 中的 `CLASSROOM_DOMAIN` 改成实际域名，并把 `CLASSROOM_BOOTSTRAP_KEY` 改为至少 32 位的随机字符串。`QWEN_API_KEY` 可先留空。

Caddy 会自动申请和续期 HTTPS 证书。浏览器打开 `https://你的域名`，首次进入会显示“首次建立教师课程”：填写课程名称、共享教师密码和 `.env` 中的初始化密钥。之后老师只使用课程名和共享密码登录。

## 课堂使用

1. 老师在网页新建课堂，得到六位课堂码。
2. 学生在 AM-LINK 左侧打开“加入课堂”，填写同一个 HTTPS 地址和六位课堂码。
3. 学生匿名提问、点赞或点“一键没听懂”；老师网页每两秒刷新统计并可广播、置顶、标记已讲解。
4. 下课时老师点“结束课堂”，历史问题仍保留；“永久删除”才会删除数据。

## 更新与备份

```bash
docker compose up -d --build
docker run --rm -v deploy_classroom-data:/data -v "$PWD":/backup alpine \
  cp /data/classrooms.db /backup/classrooms-$(date +%F).db
```

不要把 `.env`、数据库备份或真实千问 Key 提交到代码仓库或发给学生。

## 当前容量边界

这一版使用单服务器 SQLite，适合先让一个班级试用并验证 100 名左右学生的需求。若以后要多台服务器同时承载多个大课堂，再迁移到 PostgreSQL 和 SignalR Redis backplane；`postgres-schema.sql` 已保留结构草案。
