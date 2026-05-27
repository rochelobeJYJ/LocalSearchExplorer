# LocalSearchExplorer

Windows 파일 탐색기의 검색이 답답할 때 쓰는 로컬 파일 검색 프로그램입니다.

사용자가 추가한 폴더만 인덱싱하고, 파일명·폴더명·경로·확장자·수정일·크기·문서 내용까지 한곳에서 검색할 수 있습니다. 인덱스는 내 PC의 SQLite DB에만 저장됩니다.

## 다운로드

최신 설치 파일은 GitHub Releases에서 받을 수 있습니다.

- [최신 릴리즈 다운로드](https://github.com/rochelobeJYJ/LocalSearchExplorer/releases/latest)
- 설치 파일 이름: `LocalSearchExplorer-Setup-버전.exe`
- 무결성 확인용 SHA256 파일도 함께 제공합니다.

Windows에서 설치 파일 실행 시 SmartScreen 경고가 나올 수 있습니다. 아직 코드 서명 인증서를 적용하지 않았기 때문입니다.

## 주요 기능

- 원하는 폴더만 검색 위치로 등록
- 왼쪽 폴더 트리에서 실제 파일 탐색기처럼 검색 기준 폴더 선택
- 현재 검색 기준을 breadcrumb와 상태줄에 표시
- 파일명, 폴더명, 전체 경로, 확장자, 파일/폴더 유형 검색
- `size:`, `modified:`, `content:` 같은 고급 검색식 지원
- 공백 무시 검색 지원
- 결과 목록에서 연번, 이름, 종류, 크기, 수정일, 매칭 위치, 전체 경로 표시
- 검색 결과 가로 스크롤과 컬럼 크기 조절 지원
- 선택 항목 열기, 상위 폴더 열기, 탐색기에서 보기, 경로 복사
- 이름 변경, 삭제, 폴더 제외, 다시 인덱싱
- 선택형 문서 내용 인덱싱
- GitHub 최신 릴리즈 기반 업데이트 확인

## 기본 사용법

1. 앱을 실행합니다.
2. 왼쪽에서 `폴더 추가`를 눌러 검색할 폴더를 등록합니다.
3. 등록한 폴더가 스캔되면 검색창에 검색어를 입력합니다.
4. 왼쪽 폴더 트리에서 검색 기준 폴더를 바꿀 수 있습니다.
5. `하위 폴더 포함`을 끄면 현재 선택한 폴더 바로 아래 항목만 검색합니다.

문서 내부 텍스트까지 검색하려면 `내용 인덱싱`을 먼저 실행한 뒤 `내용 검색`을 켜야 합니다. 내용 인덱싱은 시간이 걸릴 수 있으므로 필요한 폴더에서만 사용하는 것을 권장합니다.

## 검색식 예시

```text
계약서
계약서 & !초안
계약서, 견적서
"연차 신청서"
ext:pdf
type:folder
name:보고서
path:2026
size:>10MB
modified:month
content:"계약 금액"
```

자세한 문법은 [검색 문법 문서](docs/query-syntax.md)를 참고하세요.

## 지원 형식

파일명과 경로 검색은 모든 파일에 대해 동작합니다.

내용 인덱싱은 현재 다음 형식을 지원합니다.

- 텍스트: `txt`, `md`, `csv`, `log`
- 문서: `pdf`, `docx`, `xlsx`, `xls`, `hwpx`
- 압축 파일 목록: `zip`

일부 암호화 문서, 손상 파일, 너무 큰 파일은 내용 인덱싱에서 제외될 수 있습니다. 실패 항목은 인덱스 상태에 반영됩니다.

## 업데이트

앱의 `업데이트` 버튼은 GitHub 최신 릴리즈를 확인합니다.

업데이트가 있으면 설치 파일과 SHA256 파일을 내려받아 무결성을 확인한 뒤 설치 파일을 실행합니다.

## 개인정보와 네트워크

- 검색 인덱스는 `%LOCALAPPDATA%\LocalSearchExplorer\index.db`에 저장됩니다.
- 검색 대상 파일이나 인덱스 내용은 외부 서버로 전송하지 않습니다.
- 네트워크는 사용자가 `업데이트`를 누를 때 GitHub 릴리즈 확인과 설치 파일 다운로드에만 사용됩니다.

## 단축키

- `Ctrl+L`: 검색창으로 이동
- `Enter`: 선택 항목 열기
- `Ctrl+Enter`: 상위 폴더 열기
- `F2`: 이름 변경
- `Delete`: 삭제
- `F5`: 현재 검색 위치 다시 스캔
- `Ctrl+C`: 선택 항목 경로 복사
- `Alt+Enter`: 속성 열기
- `Esc`: 검색어 지우기

## 개발자용

필요 환경:

- Windows 10/11
- .NET 8 SDK

실행:

```powershell
dotnet run --project .\src\LocalSearch.App\LocalSearch.App.csproj
```

테스트:

```powershell
dotnet test
```

설치 파일 생성:

```powershell
.\tools\Build-Installer.ps1
```

프로젝트 구조:

```text
LocalSearchExplorer/
 ├─ src/
 │  ├─ LocalSearch.App/
 │  └─ LocalSearch.Core/
 ├─ tests/
 ├─ docs/
 ├─ installer/
 └─ tools/
```

## 문서

- [검색 문법](docs/query-syntax.md)
- [지원 형식](docs/supported-formats.md)
- [아키텍처](docs/architecture.md)
- [수동 테스트 체크리스트](docs/manual-test-checklist.md)

## 라이선스

[MIT License](LICENSE)
