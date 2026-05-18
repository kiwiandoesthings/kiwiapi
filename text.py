import sys
import json
import AO3
import json

def main():
    sys.stdout.reconfigure(encoding='utf-8')

    workId = sys.argv[1]
    page = int(sys.argv[2])
    try:
        work = AO3.Work(workId)
        print(json.dumps({
            "title": work.title,    
            "chapter": page + 1,
            "content": work.chapters[page].text,
            "chapterCount": work.nchapters
        }))
    except Exception as e:
        print(json.dumps({"error": str(e)}))

if __name__ == '__main__':
    main()