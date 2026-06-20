# ChatOps

Hệ thống ChatOps hỗ trợ quản lý, giám sát và triển khai ứng dụng container thông qua chatbot.

## Môi trường triển khai

Hệ thống được thiết kế để triển khai trên Ubuntu Server và bao gồm các thành phần chính:

* Docker Engine
* Docker Compose v2
* .NET SDK 8.0
* Redis
* PostgreSQL
* OpenResty
* Git

---

# 1. Cài đặt các thành phần nền tảng

Thực hiện trên tất cả các node:

```bash
sudo apt update

sudo apt install -y docker.io
sudo systemctl enable docker
sudo systemctl start docker
sudo usermod -aG docker $USER

sudo apt install -y dotnet-sdk-8.0

sudo apt install -y docker-compose-v2 git
```

Đăng nhập Docker Hub (nếu sử dụng image riêng):

```bash
docker login
```

---

# 2. Cài đặt OpenResty (API Gateway)

Thực hiện trên tất cả các node.

OpenResty đóng vai trò API Gateway và Load Balancer trung tâm của hệ thống.

```bash
wget -qO - https://openresty.org/package/pubkey.gpg | sudo gpg --dearmor -o /usr/share/keyrings/openresty.gpg

echo "deb [signed-by=/usr/share/keyrings/openresty.gpg] http://openresty.org/package/ubuntu $(lsb_release -sc) main" | sudo tee /etc/apt/sources.list.d/openresty.list

sudo apt update

sudo apt install openresty -y

sudo mv /usr/local/openresty/nginx/conf/nginx.conf \
/usr/local/openresty/nginx/conf/nginx.conf.bak
```

---

# 3. Cài đặt Redis

Thực hiện trên node chính.

Redis được sử dụng để:

* Lưu trạng thái phiên làm việc.
* Đồng bộ SignalR giữa các Backend.
* Lưu thông tin kết nối.
* Hỗ trợ thuật toán cân bằng tải.

```bash
sudo apt install redis-server -y
```

Cấu hình:

```bash
sudo nano /etc/redis/redis.conf
```

Chỉnh sửa:

```conf
bind 0.0.0.0 -::1
protected-mode no
```

Khởi động lại Redis:

```bash
sudo systemctl restart redis-server
```

---

# 4. Cài đặt PostgreSQL

Thực hiện trên node chính.

PostgreSQL được sử dụng để lưu trữ dữ liệu hệ thống.

```bash
sudo apt update

sudo apt install -y postgresql postgresql-contrib

sudo systemctl start postgresql

sudo systemctl enable postgresql
```

Tạo Database và User:

```bash
sudo -i -u postgres psql
```

```sql
CREATE USER sa WITH PASSWORD 'your_password';

CREATE DATABASE chatopsdb OWNER sa;
```

Cấu hình truy cập từ xa:

```bash
sudo nano /etc/postgresql/16/main/postgresql.conf
```

```conf
listen_addresses = '*'
```

```bash
sudo nano /etc/postgresql/16/main/pg_hba.conf
```

Thêm:

```conf
host all all 0.0.0.0/0 scram-sha-256
```

Khởi động lại PostgreSQL:

```bash
sudo systemctl restart postgresql
```

---

# 5. Triển khai mã nguồn từ GitHub

Thực hiện trên tất cả các node.

```bash
cd ~

git clone https://github.com/giahieu3327/ChatOps.git
```

---

# 6. Thiết lập Symlink và phân quyền

Tạo liên kết động giữa mã nguồn và dịch vụ hệ thống.

```bash
sudo mkdir -p /var/www/ChatOps

sudo ln -s ~/ChatOps/frontend /var/www/ChatOps/frontend

sudo ln -s ~/ChatOps/OpenResty/nginx.conf \
/usr/local/openresty/nginx/conf/nginx.conf
```

Phân quyền:

```bash
sudo chown -R www-data:www-data /var/www/ChatOps/frontend

sudo find /var/www/ChatOps/frontend -type d -exec chmod 755 {} +

sudo find /var/www/ChatOps/frontend -type f -exec chmod 644 {} +

sudo chmod 755 /var/www
sudo chmod 755 /var/www/ChatOps
```

Kiểm tra cấu hình OpenResty:

```bash
sudo /usr/local/openresty/nginx/sbin/nginx -t

sudo /usr/local/openresty/nginx/sbin/nginx -s reload
```

---

# 7. Khởi tạo dịch vụ Systemd

## ChatOps Backend

Tạo file:

```text
/etc/systemd/system/chatops-backend.service
```

Sau khi tạo service:

```bash
sudo systemctl daemon-reload

sudo systemctl enable chatops-backend

sudo systemctl start chatops-backend
```

Kiểm tra trạng thái:

```bash
sudo systemctl status chatops-backend
```

---

# 8. Cấu hình Sudoers

Cho phép Backend thực thi các thao tác quản trị hệ thống.

```bash
sudo visudo
```

Thêm:

```conf
chatopsnode1 ALL=(ALL) NOPASSWD: /usr/bin/systemctl *
chatopsnode1 ALL=(ALL) NOPASSWD: /usr/sbin/nginx
chatopsnode1 ALL=(ALL) NOPASSWD: /usr/local/openresty/bin/openresty
```

---

# 9. Thiết lập Bash Aliases

Bổ sung các hàm quản trị vào:

```bash
~/.bashrc
```

Sau khi chỉnh sửa:

```bash
source ~/.bashrc
```

---

# Thành phần tùy chọn

Các thành phần dưới đây không bắt buộc để hệ thống hoạt động nhưng hỗ trợ quản trị trực quan.

## Redis Commander

```bash
sudo apt update

sudo apt install nodejs npm -y

sudo npm install -g redis-commander
```

## pgAdmin 4

```bash
sudo apt install pgadmin4-web -y
```

Sử dụng để quản trị PostgreSQL thông qua giao diện Web.
