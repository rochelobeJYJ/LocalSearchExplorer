# Local File Search Explorer

Windows에서 사용자가 지정한 폴더만 로컬로 인덱싱하고, 파일명·폴더명·경로·문서 내용을 검색하는 WPF 데스크톱 앱이다. 인덱스는 사용자 PC의 SQLite DB에만 저장하며 외부 서버 전송, 원격 분석, 인터넷 의존 기능은 넣지 않았다.

## 현재 구현 범위

- C# / .NET 8 / WPF / SQLite 구조
- `LocalSearch.Core`와 `LocalSearch.App` 분리
- 사용자가 선택한 루트 폴더 및 하위 파일·폴더 스캔
- 제외 폴더 규칙 적용
- Query AST 기반 검색 파서
- `&`, `,`, `!`, `-`, 따옴표, 괄호, `re:/.../` 지원
- `ext:`, `type:`, `name:`, `path:`, `size:`, `modified:`, `content:` 지원
- 공백 무시 검색용 정규화 컬럼
- 선택형 파일 내용 인덱싱
- TXT/MD/CSV/PDF/DOCX/XLS/XLSX/HWPX/ZIP 파일명 추출 지원
- 파일/폴더 구분, 크기, 수정일, 전체 경로, 매칭 위치 표시
- 검색 위치 TreeView 브라우저와 breadcrumb 기반 현재 검색 기준 표시
- 선택 폴더 기준 검색, 하위 폴더 포함/현재 폴더만 검색 범위 전환
- 루트별 항목 수, 파일/폴더 수, 내용 인덱스 성공/실패/대기 상태 표시
- 검색 결과 연번 표시
- 우클릭 메뉴: 열기, 상위 폴더, 탐색기에서 보기, 속성, 복사, 이름 변경, 삭제, 폴더 제외, 다시 인덱싱, 터미널
- 단축키: Ctrl+L, Enter, Ctrl+Enter, F2, Delete, F5, Ctrl+C, Alt+Enter, Esc
- 검색 위치 제거, 내용 인덱스 삭제, 모든 인덱스 삭제
- 설치형 EXE 패키징 및 GitHub 최신 릴리즈 기반 업데이트 확인
- `--root "경로"` CLI 인자로 시작 시 검색 루트 자동 등록
- 단위 테스트

## 프로젝트 구조

```text
LocalSearchExplorer/
 ├─ src/
 │  ├─ LocalSearch.App/
 │  └─ LocalSearch.Core/
 ├─ tests/
 │  └─ LocalSearch.Tests/
 ├─ docs/
 ├─ Directory.Build.props
 ├─ Directory.Solution.props
 ├─ LocalSearchExplorer.sln
 ├─ LICENSE
 └─ README.md
```

## 실행 방법

.NET 8 SDK가 설치된 Windows 환경에서 실행한다.

```powershell
cd LocalSearchExplorer
dotnet restore
dotnet build
dotnet run --project .\src\LocalSearch.App\LocalSearch.App.csproj
```

특정 폴더를 바로 검색 위치로 열기:

```powershell
dotnet run --project .\src\LocalSearch.App\LocalSearch.App.csproj -- --root "D:\업무"
```

테스트:

```powershell
dotnet test
```

설치 파일 생성:

```powershell
.\tools\Build-Installer.ps1
```

설치 파일은 `artifacts\installer\LocalSearchExplorer-Setup-0.4.0.exe`에 생성된다. GitHub 배포를 시작할 때는 `version.json`의 `githubRepo` 값을 `소유자/저장소` 형식으로 채우고, 설치 파일과 `LocalSearchExplorer-Setup-0.4.0.exe.sha256`을 GitHub 최신 릴리즈 자산으로 함께 올리면 앱의 `업데이트 확인` 기능이 활성화된다.

## 인덱스 위치

```text
%LOCALAPPDATA%\LocalSearchExplorer\index.db
```

앱은 사용자가 직접 추가한 폴더만 스캔한다.

## 검색 위치와 내용 인덱스

왼쪽 `검색 위치` TreeView에서 루트나 하위 폴더를 선택하면 그 폴더가 현재 검색 기준이 된다. 상단 breadcrumb와 상태줄에 현재 기준이 표시되며, `하위 폴더 포함`을 끄면 선택한 폴더의 바로 아래 항목만 검색한다.

파일명/경로 검색은 폴더를 추가하거나 다시 스캔하면 바로 사용할 수 있다. 문서 내부 텍스트 검색은 왼쪽 `인덱스 관리`의 `내용 인덱싱`을 실행한 뒤 `내용 검색`을 켜야 한다.

## 문서

- [검색 문법](docs/query-syntax.md)
- [아키텍처](docs/architecture.md)
- [지원 형식](docs/supported-formats.md)
- [수동 테스트 체크리스트](docs/manual-test-checklist.md)

## 다음 단계

1. 중복 파일 후보 찾기와 해시 정밀 검사
2. 저장된 검색 조건과 검색 히스토리
3. 탐색기 우클릭 메뉴 설치 스크립트
4. HWP 바이너리 전용 파서 검토
5. 업데이트 설치 파일 해시 검증
