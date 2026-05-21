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
            allUsernames = [author.username for author in result.authors]

            if len(allUsernames) > 0:
                authorNames = ", ".join(allUsernames)
            else:
                authorNames = "Anonymous"
            storyList.append({
                "id": result.id,
                "info": result.title + ", by " + authorNames
            })
        print(json.dumps(storyList))
    except Exception as e:
        print(json.dumps({"error": str(e)}))

if __name__ == '__main__':
    main()