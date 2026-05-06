import socket
import threading
import re

def handle_client(client_socket):
    try:
        request = client_socket.recv(4096).decode('utf-8', errors='ignore')
        if not request:
            return
        
        lines = request.split('\r\n')
        first_line = lines[0].split(' ')
        if len(first_line) < 3:
            return
        
        method = first_line[0]
        full_url = first_line[1]
        http_version = first_line[2]
        
        match = re.match(r'http://([^:/]+):?(\d*)(/.*)?', full_url)
        if not match:
            return
            
        host = match.group(1)
        port = int(match.group(2)) if match.group(2) else 80
        path = match.group(3) if match.group(3) else '/'
        
        new_first_line = "{} {} {}".format(method, path, http_version)
        new_request = new_first_line + '\r\n' + '\r\n'.join(lines[1:]) + '\r\n'
        
        target_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        target_socket.connect((host, port))
        target_socket.sendall(new_request.encode('utf-8'))
        
        # Логировать только один раз, после получения статуса
        logged = False
        
        # Потоковая пересылка: клиент -> сервер уже отправлен, теперь сервер -> клиент
        while True:
            chunk = target_socket.recv(4096)
            if not chunk:
                break
            if not logged:
                # Парсим статус из первого пакета
                resp_part = chunk.decode('utf-8', errors='ignore').split('\r\n')[0]
                parts = resp_part.split(' ')
                status_code = parts[1] if len(parts) >= 2 else "000"
                status_text = ' '.join(parts[2:]) if len(parts) > 2 else ""
                print("{} - {} {}".format(full_url, status_code, status_text))
                logged = True
            client_socket.sendall(chunk)
        
        target_socket.close()
        
    except Exception as e:
        print("Error: {}".format(e))
    finally:
        client_socket.close()

def main():
    server_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server_socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server_socket.bind(('127.0.0.2', 8080))
    server_socket.listen(5)
    
    print("Proxy server started on 127.0.0.2:8080")
    
    while True:
        client_socket, addr = server_socket.accept()
        thread = threading.Thread(target=handle_client, args=(client_socket,))
        thread.daemon = True
        thread.start()

if __name__ == "__main__":
    main()
