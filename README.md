🚢 AutoCAD .NET Plugin Template
Template chuẩn hóa dành cho việc phát triển các công cụ trên nền tảng AutoCAD .NET API. Bộ khung này được xây dựng dựa trên kiến trúc Module hóa và nguyên lý Clean Code, giúp bóc tách dữ liệu hình học (MTO) chính xác và nhanh chóng.

🏗️ Cấu trúc dự án (Architecture)
Dự án được phân chia thành các Module độc lập để dễ dàng bảo trì và mở rộng:

📁 Models: Chứa các lớp định nghĩa đối tượng (POCO). Tách biệt hoàn toàn với thư viện AutoCAD.

📁 Services: "Trái tim" của Plugin. Chứa ExtractionService thực hiện logic

📁 UI: Giao diện người dùng dựa trên WPF tích hợp vào AutoCAD PaletteSet.

📁 Utilities: Các hàm tiện ích dùng chung (Tính COG, đổi đơn vị, định dạng tọa độ WCS).

📁 Commands: Nơi đăng ký lệnh (CommandMethod) và quản lý vòng đời của Palette (Singleton).

Test quá trình checkin = Commit (luôn phải có comments xem là nội dung thay đổi là gì)
Quá trình checkout = Pull (kéo dự án từ Github về local)

B1: Trước khi làm thì luôn PULL về để đồng bộ hóa với hệ thống
B2: Khi kết thúc phiên làm việc thì cần Commit & Sync để đồng bộ lên hệ thống
Note: Trường hợp xảy ra Conflict

- Nhiều hơn 1 người cùng sửa 1 nội dung và up lên hệ thống?

* Phân phạm vi công việc cụ thể, tránh chồng lấn
* Nếu có chồng lấn, Github cho phép tất cả các update đó đều được thực hiện đồng bộ nhưng báo Conflict trên hệ thống, khi đó admin sẽ quyết định

Module 1 <--> function a
Module 2 <--> function a
Module 3 <--> function a
Nhưng khi cần thay đổi function a để các hệ thống về sau đơn giản hơn, tối ưu hơn thì một số user có thể tùy chỉnh function a này?

Test chia Branch để up lên hệ thống chứ không trực tiếp commit thẳng lên nhánh main. Khi đó admin sẽ quyết định phiên bản mới có ok không, nếu có thì admin sẽ merge vào nhánh main, nếu không thì sẽ reject để làm lại

Test pull from local
