using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

// OpenCV 사용을 위한 using
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System.Windows.Threading;
using System.Net;
using System.Net.Sockets;
using System.IO;

namespace WebCamTest
{
    /// <summary>
    /// MainWindow.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class MainWindow : System.Windows.Window
    {
        VideoCapture cam;
        Mat frame;
        DispatcherTimer timer;
        bool is_initCam, is_initTimer;
        Mat previousFrame;
        private TcpClient client;
        private NetworkStream stream;
        private DateTime lastCaptureTime = DateTime.MinValue; // 마지막 캡처 시간
        private TimeSpan captureDelay = TimeSpan.FromSeconds(10); // 다음 캡처까지의 딜레이 시간 (N초로 설정)
        private bool a4DetectedFlag = false; //a4 감지 플래그
        public MainWindow()
        {
            InitializeComponent();
            this.Closed += MainWindow_Closed; // Closed 이벤트 핸들러 등록


            // 서버에 연결하는 로직
            ConnectToServer();
        }

        private async Task ConnectToServer()
        {
            try
            {
                client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", 11000);
                stream = client.GetStream();
                Console.WriteLine("Connected to server.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception caught in process: {ex}");
            }
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            // 스트림과 클라이언트를 안전하게 닫기
            stream?.Close();
            client?.Close();
        }

        private async void windows_loaded(object sender, RoutedEventArgs e)
        {
            // 카메라 및 타이머 초기화
            is_initCam = init_camera();
            is_initTimer = init_Timer(0.01);

            if (is_initTimer && is_initCam)
            {
                previousFrame = new Mat();
                timer.Start();
            }
        }

        private bool init_Timer(double interval_ms)
        {
            try
            {
                timer = new DispatcherTimer();

                timer.Interval = TimeSpan.FromMilliseconds(interval_ms);
                timer.Tick += new EventHandler(timer_tick);

                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool init_camera()
        {
            try
            {
                // 0번 카메라로 VideoCapture 생성 (카메라가 없으면 안됨)
                cam = new VideoCapture(0);
                cam.FrameHeight = (int)Cam_1.Height;
                cam.FrameWidth = (int)Cam_1.Width;

                // 카메라 영상을 담을 Mat 변수 생성
                frame = new Mat();
                return true;
            }
            catch
            {
                return false;
            }
        }

        //a4 용지 크기만
        private void timer_tick(object sender, EventArgs e)
        {
            if (!cam.Read(frame)) return; // 프레임 읽기 실패 시 반환

            DetectAndCaptureWhiteObject(frame); // 흰색 사각형 감지 및 캡처 메서드 호출

            // 화면에 프레임 표시
            Dispatcher.Invoke(() => // UI 스레드에서 실행 보장
            {
                Cam_1.Source = WriteableBitmapConverter.ToWriteableBitmap(frame);
            });
        }

        //사진 찍기, 캡쳐 클릭 메서드를 따로 빼냄
        private async Task CaptureImage(Mat frame, OpenCvSharp.Rect rect)
        {
            // 감지된 A4 용지의 테두리에 해당하는 범위만 잘라냄
            Mat croppedImage = new Mat(frame, rect);

            string fileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.jpg"; //jpg 파일로 저장 받게함
            string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string fullPath = System.IO.Path.Combine(folderPath, fileName);

            // 잘라낸 현재 이미지를 저장
            Cv2.ImWrite(fullPath, croppedImage);

            Console.WriteLine($"Image saved: {fullPath}");
            //파일전송 실행
            await SendImageInfoToServerAsync(fullPath); 
        }

        //A4 용지 검출 (크기, 흰색)
        private void DetectAndCaptureWhiteObject(Mat src)
        {
            // HSV 색공간으로 변환: 사진을 HSV 색공간으로 변환해, 색상을 더 쉽게 분석
            Mat hsv = new Mat();
            Cv2.CvtColor(src, hsv, ColorConversionCodes.BGR2HSV);

            // 흰색을 감지하기 위한 HSV 범위 정의: 흰색은 HSV에서 특정 범위에 속합니다.
            // 여기서는 밝기(Value)가 높고, 채도(Saturation)가 낮은 영역을 흰색으로 정의합니다.
            Scalar lowerWhite = new Scalar(0, 0, 183); // HSV에서 흰색의 최소 범위
            Scalar upperWhite = new Scalar(180, 25, 255); // HSV에서 흰색의 최대 범위
            Mat mask = new Mat();
            Cv2.InRange(hsv, lowerWhite, upperWhite, mask);

            // 윤곽선 찾기: 마스크를 사용하여 흰색 영역의 윤곽선을 찾기
            Cv2.FindContours(mask, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            bool currentA4Detected = false;

            foreach (var contour in contours)
            {
                // 윤곽선을 둘러싸는 최소 사각형 구하기
                var rect = Cv2.BoundingRect(contour);
                // 사각형의 가로세로 비율
                double aspectRatio = (double)rect.Width / rect.Height;
                // 윤곽선의 면적을 계산
                double area = Cv2.ContourArea(contour);
                // A4 용지의 가로세로 비율(루트 2)
                double a4AspectRatio = Math.Sqrt(2);
                // 감지할 최소 면적을 설정합니다.
                double areaThreshold = 3000;

                // 윤곽선이 A4 용지의 비율과 면적 조건을 만족하는지 확인
                if (Math.Abs(aspectRatio - a4AspectRatio) < 0.15 && area > areaThreshold)
                {
                    // 중심점 계산
                    OpenCvSharp.Point center = new OpenCvSharp.Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

                    // 캡처 조건 확인 (예: 중심점이 화면의 중앙에 위치하는 경우)
                    // 화면 중앙에 근접할 때만 캡처하도록 설정
                    if (IsCenterCloseToScreenCenter(center, src.Size()))
                    {
                        if (!a4DetectedFlag && (DateTime.Now - lastCaptureTime) > captureDelay)
                        {
                            CaptureImage(src, rect);
                            lastCaptureTime = DateTime.Now;
                            a4DetectedFlag = true;
                        }
                        Cv2.Rectangle(src, rect, new Scalar(0, 255, 0), 2);
                        break;
                    }
                }
            }

            // 프레임에서 A4 용지를 감지하지 못했을 경우 플래그를 초기화
            if (!currentA4Detected)
            {
                a4DetectedFlag = false;
            }

            // 카메라 UI 컴포넌트에 프레임을 업데이트
            Dispatcher.Invoke(() =>
            {
                Cam_1.Source = WriteableBitmapConverter.ToWriteableBitmap(src);
            });
        }
        //중심점이 제대로 왔을 때 
        private bool IsCenterCloseToScreenCenter(OpenCvSharp.Point center, OpenCvSharp.Size size)
        {
            var screenCenter = new OpenCvSharp.Point(size.Width / 2, size.Height / 2);
            var distance = Math.Sqrt(Math.Pow(center.X - screenCenter.X, 2) + Math.Pow(center.Y - screenCenter.Y, 2));

            // 중심점과 화면 중앙 사이의 거리가 임계값 이내인지 확인
            //임계값은 사용자 편의에 따라서 조정 
            return distance < 30; // 예: 중심점이 화면 중앙에서 N픽셀 이내에 위치
        }

        // 파일전송 실행
        private async Task SendImageInfoToServerAsync(string imagePath)
        {
            try
            {
                // 기존 연결된 stream을 사용하여 파일 정보와 데이터 전송
                string fileName = System.IO.Path.GetFileName(imagePath);
                byte[] fileData = File.ReadAllBytes(imagePath);
                int fileLength = fileData.Length;
                string fileInfo = $"{fileName}^{fileLength}";
                byte[] fileInfoBytes = Encoding.UTF8.GetBytes(fileInfo);

                await stream.WriteAsync(fileInfoBytes, 0, fileInfoBytes.Length);
                Console.WriteLine($"File info sent: {fileInfo}");

                // 서버로부터 "ok" 응답 대기
                byte[] response = new byte[1024];
                int bytesRead = await stream.ReadAsync(response, 0, response.Length);
                string responseStr = Encoding.UTF8.GetString(response, 0, bytesRead).Trim();

                if (responseStr.ToLower().Equals("ok"))
                {
                    await Task.Delay(500); // 500ms 대기
                    await stream.WriteAsync(fileData, 0, fileData.Length);
                    Console.WriteLine("File sent successfully.");

                    bytesRead = await stream.ReadAsync(response, 0, response.Length);
                    responseStr = Encoding.UTF8.GetString(response, 0, bytesRead).Trim();
                    HandleReceiveData(responseStr);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception caught in process: {ex}");
            }
        }

        //데이터 수신 처리 메서드
        private async void HandleReceiveData(string data)
        {
            // 데이터 처리 로그
            Console.WriteLine("Handling data: " + data);

            // 이 부분은 백그라운드 스레드에서 실행될 수 있습니다.
            string[] tokens = data.Split('^');
            if (tokens.Length == 3)
            {
                // 메인 UI 스레드에서 실행해야 하는 UI 업데이트 로직
                Dispatcher.Invoke(() =>
                {
                    Console.WriteLine("Invalid data format received: " + data);
                    Console.WriteLine($"Processing data: num={tokens[0]}, color={tokens[1]}, shape={tokens[2]}");
                    UpdateUI(tokens);
                });
            }
        }


        private void UpdateUI(string[] tokens)
        {
            string num = tokens[0];
            string color = tokens[1];
            string shape = tokens[2];

            //UI ListBox 업데이트
            Dispatcher.Invoke(() =>
            {
                //색깔 업데이트
                switch (color)
                {
                    case "red":
                        RedBox.Items.Add(shape);
                        break;

                    case "blue":
                        BlueBox.Items.Add(shape);
                        break;

                    case "green":
                        GreenBox.Items.Add(shape);
                        break;

                    case "orange":
                        OrangeBox.Items.Add(shape);
                        break;

                    case "skyblue":
                        SkyBlueBox.Items.Add(shape);
                        break;

                    case "yellow":
                        YellowBox.Items.Add(shape);
                        break;

                    case "purple":
                        PurpleBox.Items.Add(shape);
                        break;

                    case "pink":
                        PinkBox.Items.Add(shape);
                        break;

                    default:
                        break;
                }

                //모양 업데이트
                switch (shape)
                {
                    case "square":
                        SquareBox.Items.Add(color);
                        break;

                    case "triangle":
                        TriangleBox.Items.Add(color);
                        break;

                    case "circle":
                        CircleBox.Items.Add(color);
                        break;

                    case "rectangle":
                        RectangleBox.Items.Add(color);
                        break;

                    case "equilateral triangle":
                        RightTriangleBox.Items.Add(color);
                        break;

                    case "halfcircle":
                        HalfCircleBox.Items.Add(color);
                        break;

                    case "star!":
                        StarBox.Items.Add(color);
                        break;

                    case "pentagon":
                        PentagonBox.Items.Add(color);
                        break;

                    default:
                        break;
                }

                //수량 label content 변화 업데이트
                //색깔label
                RedNum.Content = RedBox.Items.Count.ToString();
                BlueNum.Content = BlueBox.Items.Count.ToString();
                GreenNum.Content = GreenBox.Items.Count.ToString();
                OrangeNum.Content = OrangeBox.Items.Count.ToString();
                SkyBlueNum.Content = SkyBlueBox.Items.Count.ToString();
                YellowNum.Content = YellowBox.Items.Count.ToString();
                PurpleNum.Content = PurpleBox.Items.Count.ToString();
                PinkNum.Content = PinkBox.Items.Count.ToString();
                //도형 label
                SquareNum.Content = SquareBox.Items.Count.ToString();
                TriangleNum.Content = TriangleBox.Items.Count.ToString();
                CircleNum.Content = CircleBox.Items.Count.ToString();
                RectangleNum.Content = RectangleBox.Items.Count.ToString();
                RightTriangleNum.Content = RightTriangleBox.Items.Count.ToString();
                HalfCircleNum.Content = HalfCircleBox.Items.Count.ToString();
                StarNum.Content = StarBox.Items.Count.ToString();
                PentagonNum.Content = PentagonBox.Items.Count.ToString();

            });
        }
    }
}
