import sys
import json
import AO3

def main():
    sys.stdout.reconfigure(encoding='utf-8')

    query = sys.argv[1]
    page = int(sys.argv[2])
    try:
        storyList = []
        search = AO3.Search(any_field=query)
        search.page = page
        search.update()
        for result in search.results:
            storyList.append({
                "id": result.id,
                "info": result.title + ", by " + result.author
            })
        print(json.dumps(storyList))
    except Exception as e:
        print(json.dumps({"error": str(e)}))

if __name__ == '__main__':
    main()